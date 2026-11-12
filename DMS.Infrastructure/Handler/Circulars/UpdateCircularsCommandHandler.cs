using DMS.Data.EF;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Circulars;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Data.SqlTypes;

namespace DMS.Infrastructure.Query.Circulars;

public class UpdateCircularsCommandHandler : IRequestHandler<UpdateCircularsCommand, long>
{
    private readonly CircularsContext _context;
    private readonly AttachmentStorage _storage;

    public UpdateCircularsCommandHandler(CircularsContext context, AttachmentStorage storage)
    {
        _context = context;
        _storage = storage;
    }

    private static AttachmentRef RefOf(CIR_Blob b)
        => new(b.CIR_BlobID, b.FileName, null, b.HQ_Storage_Option, b.HQ_Local_Folder, b.Cloud_ContainerName, b.Cloud_Directory);

    private static void Apply(CIR_Blob b, StoredAttachment s)
    {
        b.BlobContents = s.BlobContents;
        b.Cloud_ContainerName = s.Cloud_ContainerName;
        b.Cloud_Directory = s.Cloud_Directory;
        b.HQ_Storage_Option = s.HQ_Storage_Option;
        b.HQ_Local_Folder = s.HQ_Local_Folder;
        b.HQ_DatabaseName = s.HQ_DatabaseName;
        b.VSL_Storage_Option = s.VSL_Storage_Option;
        b.VSL_DatabaseName = s.VSL_DatabaseName;
        b.VSL_Local_Folder = s.VSL_Local_Folder;
    }

    /// <summary>Whether the edit sends back the file already stored for this circular (same name, same bytes).</summary>
    private async Task<bool> IsSameFileAsync(CIR_Blob blob, DMS.Application.DTO.Circulars.EditCircularDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blob.FileName) || dto.BlobContents == null
            || !string.Equals((dto.FileName ?? "").Trim(), blob.FileName.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (blob.BlobSize is { } size && size > 0 && size != dto.BlobContents.Length) return false;
        var stored = await _storage.ReadAsync(AttachmentStorage.CircularTable, new AttachmentRef(blob.CIR_BlobID, blob.FileName,
            blob.BlobContents, blob.HQ_Storage_Option, blob.HQ_Local_Folder, blob.Cloud_ContainerName, blob.Cloud_Directory), ct);
        return stored != null && stored.AsSpan().SequenceEqual(dto.BlobContents);
    }

    /// <summary>A new CIR_Blob row for this circular, its file saved as BlobTableSettings says.</summary>
    private async Task AddBlobAsync(DMS.Application.DTO.Circulars.EditCircularDto dto, CancellationToken ct)
    {
        var newBlob = new CIR_Blob
        {
            CIR_BlobID = await _context.NextIdAsync<CIR_Blob>(ct: ct),
            CIR_MasterID = dto.CIR_MasterID,
            Origin = "HQ",
            VesselID = -1,
            Description = dto.Description ?? string.Empty,
            FileName = dto.FileName,
            FileType = dto.FileType,
            OriginalFileType = dto.OriginalFileType,
            BlobSize = dto.BlobSize,
            AddedOn = DateTime.UtcNow
        };
        if (dto.BlobContents is { Length: > 0 }) FileRules.Check(newBlob.FileName);
        Apply(newBlob, await _storage.SaveAsync(AttachmentStorage.CircularTable, newBlob.CIR_BlobID, newBlob.FileName, dto.BlobContents, ct));
        await _context.CIR_Blob.AddAsync(newBlob, ct);
    }

    public async Task<long> Handle(UpdateCircularsCommand request, CancellationToken cancellationToken)
    {
        var dto = request.EditCIR;

        var master = await _context.CIR_Master
            .FirstOrDefaultAsync(m => m.CIR_MasterID == dto.CIR_MasterID, cancellationToken);

        if (master == null)
            throw new KeyNotFoundException($"Circular {dto.CIR_MasterID} does not exist.");

        // Update master fields
        master.CIR_CategoryID = dto.CIR_CategoryID;
        master.CIR_Number = dto.CIR_Number;
        master.Reference = dto.Reference;
        master.Title = dto.Title;
        master.Status = dto.Status;
        master.Priority = dto.Priority;
        master.DateIssued = dto.DateIssued;
        master.ReleasedDate = dto.ReleasedDate;
        master.Remarks = dto.Remarks;
        master.ModifiedBy = dto.CreatedBy ?? master.ModifiedBy;
        master.ModifiedDate = DateTime.UtcNow;

        _context.CIR_Master.Update(master);

        // Stored files (local folder / cloud) that are no longer used: removed only once the changes are saved.
        var oldFiles = new List<AttachmentRef>();

        try
        {
            var blob = await _context.CIR_Blob
                .FirstOrDefaultAsync(b => b.CIR_BlobID == dto.CIR_BlobID, cancellationToken);

            if (blob != null)
            {
                var oldFile = RefOf(blob);
                if (dto.BlobContents is { Length: > 0 } && await IsSameFileAsync(blob, dto, cancellationToken))
                {
                    // The same file sent back with the form (an older page does that): nothing to save. Saving it again
                    // gave it a new AddedOn, and every vessel then downloaded the whole file again.
                }
                else if (dto.BlobContents != null && dto.BlobContents.Length > 0)
                {
                    FileRules.Check(dto.FileName);
                    blob.BlobSize = dto.BlobSize;
                    blob.FileName = dto.FileName;
                    blob.FileType = dto.FileType;
                    blob.OriginalFileType = dto.OriginalFileType;
                    blob.AddedOn = DateTime.UtcNow;
                    Apply(blob, await _storage.SaveAsync(AttachmentStorage.CircularTable, blob.CIR_BlobID, blob.FileName, dto.BlobContents, cancellationToken));

                    // The previous file, when it was kept somewhere else (another name, folder or container).
                    if (oldFile != RefOf(blob)) oldFiles.Add(oldFile);
                }
                else if (!string.IsNullOrWhiteSpace(dto.FileName) && !string.IsNullOrWhiteSpace(blob.FileName)
                         && string.Equals(dto.FileName.Trim(), blob.FileName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // No contents, but the form still has this file: only the other fields were edited. The stored file
                    // stays exactly as it is (contents, size, AddedOn), so the vessels do not download it again.
                }
                else
                {
                    // No file any more (removed in the form: empty file name).
                    oldFiles.Add(oldFile);
                    blob.FileName = null;
                    blob.FileType = null;
                    blob.OriginalFileType = null;
                    blob.BlobSize = null;
                    blob.BlobContents = null;
                    blob.Cloud_ContainerName = null;
                    blob.Cloud_Directory = null;
                    blob.HQ_Storage_Option = null;
                    blob.HQ_DatabaseName = null;
                    blob.HQ_Local_Folder = null;
                    blob.VSL_Storage_Option = null;
                    blob.VSL_DatabaseName = null;
                    blob.VSL_Local_Folder = null;
                }
                blob.Description = dto.Description ?? blob.Description;

                _context.CIR_Blob.Update(blob);
            }
            else if (dto.BlobContents != null && dto.BlobContents.Length > 0)
            {
                // create new blob entry linked to master
                await AddBlobAsync(dto, cancellationToken);
            }
        }
        catch (SqlNullValueException sqlNullEx)
        {
            // Defensive fallback when DB returns a DBNull for a column that the model expects non-null.
            // If client provided new blob contents, create a new blob record instead of failing.
            if (dto.BlobContents != null && dto.BlobContents.Length > 0)
            {
                await AddBlobAsync(dto, cancellationToken);
            }
            else
            {
                // Re-throw with more context so caller can inspect the cause and DB state.
                throw new InvalidOperationException($"Error reading CIR_Blob with ID {dto.CIR_BlobID}: {sqlNullEx.Message}", sqlNullEx);
            }
        }

        // Remove existing CIR_ToAck_Vsl rows for this master
        var existingToAck = _context.CIR_ToAck_Vsl
            .Where(x => x.CIR_MasterID == dto.CIR_MasterID);

        _context.CIR_ToAck_Vsl.RemoveRange(existingToAck);

        // Insert new rows: one per vessel per rank
        if ((dto.AllowedUserToAck != null && dto.AllowedUserToAck.Count > 0) &&
            (dto.VesselIdList != null && dto.VesselIdList.Count > 0))
        {
            List<string>? userRanksList = (dto.AllowedUserToAck != null && dto.AllowedUserToAck.Any())
                            ? dto.AllowedUserToAck.Distinct().ToList()
                            : new List<string> { string.Empty };

            var vesselIds = (dto.VesselIdList != null && dto.VesselIdList.Any())
                            ? dto.VesselIdList.Distinct().ToList()
                            : new List<int> { -1 };

            var toAckEntities = new List<CIR_ToAck_Vsl>();
            // One block of IDs for every vessel x rank row.
            long idCounter = await _context.NextIdAsync<CIR_ToAck_Vsl>(count: vesselIds.Count * userRanksList.Count, ct: cancellationToken);

            foreach (var vesselId in vesselIds)
            {
                foreach (var rank in userRanksList)
                {
                    toAckEntities.Add(new CIR_ToAck_Vsl
                    {
                        CIR_ToAck_VslID = idCounter++,
                        CIR_MasterID = dto.CIR_MasterID,
                        Origin = "HQ",
                        VesselID = vesselId,
                        UserRank = rank,
                        IsActive = 1
                    });
                }
            }

            if (toAckEntities.Count > 0)
                await _context.CIR_ToAck_Vsl.AddRangeAsync(toAckEntities, cancellationToken);
        }
        // --- End new to-ack handling ---

        // A failed save also removes a new file just stored in a local folder or the cloud.
        await _storage.SaveOrDiscardAsync(() => _context.SaveChangesAsync(cancellationToken));

        foreach (var stale in oldFiles)
            await _storage.DeleteAsync(AttachmentStorage.CircularTable, stale, cancellationToken);

        return master.CIR_MasterID;
    }
}