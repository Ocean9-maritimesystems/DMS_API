using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DMS.Application.DTO.Features;
using DMS.Application.DTO.OtherDocument;
using DMS.Application.Interface;
using DMS.Data.EF;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.OtherDocument;
using DMS.Data.EF.Query.Circulars;

namespace DMS.Application;

public class OtherDocumentService : IOtherDocumentService
{
    private readonly OtherDocumentsContext _dbContext;
    private readonly ILogger<OtherDocumentService> _logger;
    private readonly IMediator _mediator;
    private readonly SqlDb _db;
    private readonly GeneralContext _generalContext;
    private readonly AttachmentStorage _storage;

    public OtherDocumentService(OtherDocumentsContext context, ILogger<OtherDocumentService> logger, IMediator mediator, SqlDb db, GeneralContext generalContext,
        AttachmentStorage storage)
    {
        _storage = storage;
        _dbContext = context;
        _logger = logger;
        _mediator = mediator;
        _db = db;
        _generalContext = generalContext;
    }

    public async Task<List<OtherDocument_Type>> GetDocumentTypesAsync()
    {
        var query = _dbContext.OtherDocument_Type.AsQueryable();

        return await query
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.DocumentType)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<List<Vw_OtherDocument_MasterBlob>> GetRecentOtherDocumentsAsync(int limit = 10)
    {
        return await _dbContext.Vw_OtherDocument
            .OrderByDescending(v => v.UploadedOn)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<List<Vw_OtherDocument_MasterBlob>> GetDocumentsAsync(int? vesselId = null, string? documentType = null)
    {
        var query = _dbContext.Vw_OtherDocument.AsQueryable();

        if (vesselId.HasValue)
        {
            query = query.Where(v => v.VesselID == vesselId.Value || (vesselId.Value == 0 && v.VesselID == 0));
        }

        if (!string.IsNullOrWhiteSpace(documentType))
        {
            query = query.Where(v => v.DocumentType == documentType.Trim());
        }

        return await query
            .OrderByDescending(v => v.UploadedOn)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<Vw_OtherDocument_MasterBlob?> GetOtherDocumentDetailsAsync(long otherDocumentMasterID)
    {
        var doc = await _dbContext.Vw_OtherDocument
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.OtherDocumentMasterID == otherDocumentMasterID);

        // A file kept in a local folder or the cloud is loaded from there (BlobTableSettings).
        if (doc?.OtherDocument_BlobID != null && doc.BlobContents == null && !string.IsNullOrEmpty(doc.FileName))
            doc.BlobContents = await _storage.ReadAsync(AttachmentStorage.OtherDocumentTable, new AttachmentRef(doc.OtherDocument_BlobID.Value,
                doc.FileName, null, doc.HQ_Storage_Option, doc.HQ_Local_Folder, doc.Cloud_ContainerName, doc.Cloud_Directory));
        return doc;
    }

    public async Task<long> InsertOtherDocumentRecordAsync()
    {
        try
        {
            var nextId = await _dbContext.NextIdAsync<OtherDocumentMaster>();

            var entity = new OtherDocumentDto
            {
                OtherDocumentMasterID = nextId,
                DocumentTitle = string.Empty,
                DocumentNumber = string.Empty,
                VesselID = -1,
                UploadedOn = DateTime.UtcNow
            };

            // _dbContext.OtherDocument.Add(entity);
            await _dbContext.SaveChangesAsync();

            return entity.OtherDocumentMasterID;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert OtherDocument record.");
            throw;
        }
    }

    public async Task<long> AddOtherDocumentAsync(CreateOtherDocumentDto model)
    {
        if (model is null)
            throw new ArgumentNullException(nameof(model));

        var result = await _mediator.Send(new CreateOtherDocumentCommand(model));
        return result;
    }

    public async Task RemoveTempOtherDocumentAsync(long otherDocumentMasterID)
    {
        try
        {
            var entity = await _dbContext.OtherDocumentMaster
                            .Where(e => e.OtherDocumentMasterID == otherDocumentMasterID)
                            .FirstOrDefaultAsync();

            if (entity != null)
            {
                _dbContext.OtherDocumentMaster.Remove(entity);
                await _dbContext.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove OtherDocument record with id {OtherDocumentMasterID}", otherDocumentMasterID);
        }
    }

    public async Task<string> GetNextDocumentNumberAsync(long typeId)
    {
        var nextDocumentNumber = "0.0";
        var maxDocumentNumber = await _dbContext.OtherDocumentMaster
                             .Where(x => x.OtherDocument_TypeID == typeId)
                             .OrderByDescending(x => x.DocumentNumber)
                             .Select(m => m.DocumentNumber)
                             .FirstOrDefaultAsync();
        if (maxDocumentNumber != null)
        {
            string[] parts = maxDocumentNumber.Split('.');
            int major = int.Parse(parts[0]) ;
            nextDocumentNumber = $"{major}.0";
        }
        else
        {
            nextDocumentNumber = $"1.0";
        }

        var docType = await _dbContext.Set<OtherDocument_Type>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.OtherDocument_TypeID == typeId);

        if (docType == null) return "";

        var prefix = (docType.DocumentCode ?? string.Empty).Trim();
        nextDocumentNumber = $"{prefix}{nextDocumentNumber}";

        return nextDocumentNumber;
    }

    // ================================================================== Folders
    //
    // Folders for Other Documents (OtherDocument_Folder); a folder holds Other Documents only (OtherDocumentMaster.OtherDocument_FolderID).
    //   Company    - VesselID -1 (every vessel) or one vessel; synced down; on board ViewRoles / EditRoles (ranks) apply.
    //   Vessel     - one vessel's own folders (VesselID > 0), made at the office or on board; synced both ways.
    //   Department - office: VesselID -2, OwnerUserID = the office department; on board: the vessel's department folders.
    //   Personal   - VesselID -2 at the office, owner only. Personal folders made on board never leave the ship.
    // A sub-folder takes its scope, vessel and owner from its parent.

    public static readonly string[] FolderScopes = { "Company", "Vessel", "Department", "Personal" };

    private sealed class FolderRow : FolderItem
    {
        public string? Origin { get; set; }
        public string? CreatedBy { get; set; }
        public int? SortOrder { get; set; }
        public bool CanView { get; set; }
    }

    private static bool IsAdmin(OfficeUser user) => string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase);

    private static bool InList(string? csv, string? value)
        => !string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(csv) &&
           csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Any(r => string.Equals(r, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Who may open / change a folder, from the office.</summary>
    private static T Rights<T>(T f, OfficeUser user) where T : FolderItem
    {
        switch (f.Scope)
        {
            case "Personal":
                if (f is FolderRow fr1)
                    fr1.CanView = fr1.CanEdit = fr1.VesselID == SqlDb.OfficeOnly && fr1.OwnerUserID == user.UserId;
                break;
            case "Department" when f.VesselID == SqlDb.OfficeOnly:
                if (f is FolderRow fr2)
                {
                    fr2.CanView = IsAdmin(user) || string.Equals(fr2.OwnerUserID, user.Dept, StringComparison.OrdinalIgnoreCase);
                    fr2.CanEdit = fr2.CanView;
                }
                break;
            default: // Company, Vessel, and a vessel's department folders: every office user may look
                if (f is FolderRow fr3)
                {
                    fr3.CanView = true;
                    // A vessel's folders: every office user may file documents in them (office documents stay at the office).
                    fr3.CanEdit = IsAdmin(user) || fr3.Scope == "Vessel"
                                  || (fr3.Origin == SqlDb.Office && (fr3.CreatedBy == user.UserId || InList(fr3.EditRoles, user.Role)));
                }
                break;
        }
        return f;
    }

    private async Task<FolderRow?> FolderAsync(long id, OfficeUser user)
    {
        var f = (await LoadFolderRowsAsync()).FirstOrDefault(x => x.OtherDocument_FolderID == id);
        return f == null ? null : Rights(f, user);
    }

    public async Task<List<FolderItem>> GetFolderTreeAsync(OfficeUser user)
    {
        var rows = await LoadFolderRowsAsync();
        return rows
            .Where(f => f.Scope is "Company" or "Vessel"
                || (f.Scope == "Department" && (f.VesselID > 0 || f.OwnerUserID == user.Dept || IsAdmin(user)))
                || (f.Scope == "Personal" && f.VesselID == SqlDb.OfficeOnly && f.OwnerUserID == user.UserId))
            .OrderBy(f => f.Scope == "Company" ? 0 : f.Scope == "Vessel" ? 1 : f.Scope == "Department" ? 2 : 3)
            .ThenBy(f => f.SortOrder).ThenBy(f => f.FolderName)
            .Select(f => Rights(f, user)).Where(f => f.CanView).Cast<FolderItem>().ToList();
    }

    private async Task<List<FolderRow>> LoadFolderRowsAsync()
    {
        var folders = await _dbContext.FldFolders.AsNoTracking().Where(f => f.IsDeleted == 0).ToListAsync();
        var folderIds = folders.Select(f => f.OtherDocument_FolderID).ToList();
        var fileCounts = await _dbContext.OtherDocumentMaster.AsNoTracking()
            .Where(d => d.OtherDocument_FolderID.HasValue && folderIds.Contains(d.OtherDocument_FolderID.Value) && (d.IsDeleted == null || d.IsDeleted != 1))
            .GroupBy(d => d.OtherDocument_FolderID!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        var vessels = await _generalContext.VVessels.AsNoTracking().ToDictionaryAsync(v => v.VesselID, v => v.VslName);
        return folders.Select(f => new FolderRow
        {
            OtherDocument_FolderID = f.OtherDocument_FolderID, Origin = f.Origin, VesselID = f.VesselID,
            VesselName = f.VesselID > 0 ? vessels.GetValueOrDefault(f.VesselID) : f.VesselID == -1 ? "All vessels" : "Office",
            ParentID = f.ParentID, FolderName = f.FolderName, Scope = f.Scope, OwnerUserID = f.OwnerUserID,
            ViewRoles = f.ViewRoles, EditRoles = f.EditRoles, CreatedBy = f.CreatedBy,
            SortOrder = f.SortOrder, FileCount = fileCounts.GetValueOrDefault(f.OtherDocument_FolderID)
        }).ToList();
    }

    public async Task<(long? Id, string? Error)> SaveFolderAsync(long? folderId, SaveFolder request, OfficeUser user)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > 150) return (null, "A folder name of up to 150 characters is required.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (null, "The folder name has characters that are not allowed.");

        FolderRow? parent = null;
        if (request.ParentId != null)
        {
            parent = await FolderAsync(request.ParentId.Value, user);

            if (parent == null) return (null, "The parent folder was not found.");
            if (!parent.CanEdit) return (null, "You cannot add folders here.");
        }

        if (folderId != null)
        {
            var current = await FolderAsync(folderId.Value, user);
            if (current == null) return (null, "Folder not found.");
            if (!current.CanEdit) return (null, "You cannot change this folder.");

            if (parent != null && (parent.Scope != current.Scope || parent.VesselID != current.VesselID))
                return (null, "A folder can only move under a folder of the same kind and vessel.");

            if (parent != null && await IsBelowAsync(parent.OtherDocument_FolderID, current.OtherDocument_FolderID))
                return (null, "A folder cannot move into itself.");

            if (await NameTakenAsync(request.ParentId, name, current.Scope, current.VesselID, current.OwnerUserID, current.OtherDocument_FolderID))
                return (null, "There is already a folder with this name here.");

            var entity = await _dbContext.FldFolders.FirstAsync(f => f.OtherDocument_FolderID == current.OtherDocument_FolderID);
            entity.FolderName = name;
            entity.ParentID = request.ParentId;
            entity.ViewRoles = Roles(request.ViewRoles);
            entity.EditRoles = Roles(request.EditRoles);
            await _dbContext.SaveChangesAsync();
            return (current.OtherDocument_FolderID, null);
        }

        string scope;
        int vesselId;
        string? owner;
        if (parent != null)
        {
            (scope, vesselId, owner) = (parent.Scope, parent.VesselID, parent.OwnerUserID);
        }
        else
        {
            scope = FolderScopes.FirstOrDefault(s => string.Equals(s, request.Scope, StringComparison.OrdinalIgnoreCase)) ?? "";
            switch (scope)
            {
                case "Company":
                    if (!IsAdmin(user)) return (null, "Only an administrator can add company folders.");

                    vesselId = request.VesselId is > 0 ? request.VesselId.Value : SqlDb.AllVessels;
                    owner = null;
                    break;
                case "Vessel":
                    if (request.VesselId is not > 0) return (null, "Choose the vessel.");

                    vesselId = request.VesselId.Value;
                    owner = null;
                    break;
                case "Department":
                    if (string.IsNullOrWhiteSpace(user.Dept)) return (null, "Your user has no department.");

                    vesselId = SqlDb.OfficeOnly;
                    owner = user.Dept;
                    break;
                case "Personal":
                    vesselId = SqlDb.OfficeOnly;
                    owner = user.UserId;
                    break;
                default:
                    return (null, "Scope must be Company, Vessel, Department or Personal.");
            }

            if (vesselId > 0 && !await VesselExistsAsync(vesselId))
                return (null, "Vessel not found.");
        }

        if (await NameTakenAsync(request.ParentId, name, scope, vesselId, owner, null))
            return (null, "There is already a folder with this name here.");

        var id = await _db.NextIdAsync("OtherDocument_Folder");
        _dbContext.FldFolders.Add(new FldFolder
        {
            OtherDocument_FolderID = id,
            Origin = SqlDb.Office,
            VesselID = vesselId,
            ParentID = request.ParentId,
            FolderName = name,
            Scope = scope,
            OwnerUserID = owner,
            ViewRoles = Roles(request.ViewRoles),
            EditRoles = Roles(request.EditRoles),
            SortOrder = null,
            CreatedBy = user.UserId,
            CreatedOn = DateTime.UtcNow,
            IsDeleted = 0,
        });
        await _dbContext.SaveChangesAsync();
        return (id, null);
    }

    public async Task<string?> DeleteFolderAsync(long folderId, OfficeUser user)
    {
        var f = await FolderAsync(folderId, user);
        if (f == null) return "Folder not found.";
        if (!f.CanEdit) return "You cannot delete this folder.";
        if (await _dbContext.FldFolders.AnyAsync(x => x.ParentID == folderId && x.IsDeleted == 0))
            return "Delete or move the sub-folders first.";
        if (f.FileCount > 0) return "Move or delete the documents in it first.";
        await _dbContext.FldFolders.Where(x => x.OtherDocument_FolderID == folderId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDeleted, 1));
        return null;
    }

    // ------------------------------------------------------------------ Other Documents in folders
    //
    // A document added at the office is office only: IsForOfficeOnly = 1, never sent to a vessel. It keeps the vessel it is
    // about as its VesselID (-2 when added without a vessel), so it is listed under that vessel with the vessel's own documents.

    /// <summary>A folder's documents; folderId null = documents not in a folder (or in a folder that no longer exists).</summary>
    public async Task<List<FolderDocument>> GetFolderDocumentsAsync(long? folderId, int? vesselId, OfficeUser user)
    {
        if (folderId != null)
        {
            var folder = await FolderAsync(folderId.Value, user);
            if (folder is not { CanView: true }) return new List<FolderDocument>();
            var documents = await _dbContext.OtherDocumentMaster.AsNoTracking()
                .Where(d => (d.IsDeleted == null || d.IsDeleted != 1) && d.OtherDocument_FolderID == folderId)
                .ToListAsync();
            return await ProjectDocumentsAsync(documents, vesselId, orderByType: true);
        }
        var unfiled = await _dbContext.OtherDocumentMaster.AsNoTracking()
            .Where(d => (d.IsDeleted == null || d.IsDeleted != 1) && d.OtherDocument_FolderID == null && (vesselId.HasValue || d.VesselID != SqlDb.OfficeOnly))
            .ToListAsync();
        return await ProjectDocumentsAsync(unfiled, vesselId, orderByType: false);
    }

    /// <summary>Every document under a vessel, in any folder or none, without those in folders this user may not open.</summary>
    public async Task<List<FolderDocument>> GetVesselDocumentsAsync(int vesselId, OfficeUser user)
    {
        var visible = (await GetFolderTreeAsync(user)).Select(f => f.OtherDocument_FolderID).ToHashSet();
        var entities = await _dbContext.OtherDocumentMaster.AsNoTracking()
            .Where(d => (d.IsDeleted == null || d.IsDeleted != 1) && (d.VesselID == vesselId || d.VesselID == -1))
            .ToListAsync();
        var docs = await ProjectDocumentsAsync(entities, vesselId, orderByType: false);
        foreach (var d in docs)
            if (d.OtherDocument_FolderID != null && d.FolderName == null) d.OtherDocument_FolderID = null; // its folder was deleted
        return docs.Where(d => d.OtherDocument_FolderID == null || visible.Contains(d.OtherDocument_FolderID.Value)).ToList();
    }

    private async Task<List<FolderDocument>> ProjectDocumentsAsync(List<OtherDocumentMaster> entities, int? vesselId, bool orderByType)
    {
        if (vesselId.HasValue)
            entities = entities.Where(d => d.VesselID == vesselId || d.VesselID == -1).ToList();

        var typeIds = entities.Where(d => d.OtherDocument_TypeID.HasValue).Select(d => d.OtherDocument_TypeID!.Value).Distinct().ToList();
        var folderIds = entities.Where(d => d.OtherDocument_FolderID.HasValue).Select(d => d.OtherDocument_FolderID!.Value).Distinct().ToList();
        var types = await _dbContext.OtherDocument_Type.AsNoTracking().Where(t => typeIds.Contains(t.OtherDocument_TypeID)).ToDictionaryAsync(t => t.OtherDocument_TypeID);
        var folders = await _dbContext.FldFolders.AsNoTracking().Where(f => folderIds.Contains(f.OtherDocument_FolderID) && f.IsDeleted == 0).ToDictionaryAsync(f => f.OtherDocument_FolderID);
        var vesselNames = await _generalContext.VVessels.AsNoTracking().ToDictionaryAsync(v => v.VesselID, v => v.VslName);
        var documentIds = entities.Select(d => d.OtherDocumentMasterID).ToList();
        var blobs = await _dbContext.OtherDocument_Blob.AsNoTracking().Where(b => documentIds.Contains(b.OtherDocumentMasterID)).GroupBy(b => b.OtherDocumentMasterID).ToDictionaryAsync(g => g.Key, g => new { Count = g.Count(), First = g.Min(b => b.OtherDocument_BlobID) });

        var result = entities.Take(2000).Select(d =>
        {
            types.TryGetValue(d.OtherDocument_TypeID ?? 0, out var type);
            folders.TryGetValue(d.OtherDocument_FolderID ?? 0, out var folder);
            blobs.TryGetValue(d.OtherDocumentMasterID, out var blob);
            return new FolderDocument { OtherDocumentMasterID = d.OtherDocumentMasterID, VesselID = d.VesselID,
                IsForOfficeOnly = d.IsForOfficeOnly == 1,
                VesselName = d.VesselID > 0 ? vesselNames.GetValueOrDefault(d.VesselID) : d.VesselID == -1 ? "All vessels" : "Office",
                OtherDocument_FolderID = d.OtherDocument_FolderID, FolderName = folder?.FolderName, OtherDocument_TypeID = d.OtherDocument_TypeID,
                DocumentType = type?.DocumentType, DocumentNumber = d.DocumentNumber, DocumentTitle = d.DocumentTitle,
                Status = d.Status, Reference = d.Reference, Comments = d.Comments, UploadedOn = d.UploadedOn,
                UploadedBy = d.UploadedBy, Origin = d.Origin,
                FileCount = blob?.Count ?? 0, FirstFileID = blob?.First };
        });
        return (orderByType ? result.OrderBy(d => d.DocumentType).ThenBy(d => d.DocumentNumber) : result.OrderByDescending(d => d.UploadedOn)).ToList();
    }

    /// <summary>
    /// A document added at the office, in a folder or not. Always office only (IsForOfficeOnly = 1): it is never sent to a
    /// vessel. VesselID = the selected vessel (request.VesselId), or -2 when none was chosen.
    /// </summary>
    public async Task<(long? Id, string? Error)> CreateDocumentAsync(long? folderId, NewFolderDocument request, OfficeUser user)
    {
        if (folderId != null)
        {
            var folder = await FolderAsync(folderId.Value, user);
            if (folder == null) return (null, "Folder not found.");
            if (!folder.CanEdit) return (null, "You cannot add documents to this folder.");
        }
        var number = (request.DocumentNumber ?? "").Trim();
        var title = (request.Title ?? "").Trim();
        if (number.Length == 0 || number.Length > 50) return (null, "Enter a document number of up to 50 characters.");
        if (title.Length == 0 || title.Length > 500) return (null, "Enter a title of up to 500 characters.");
        if (!await _dbContext.OtherDocument_Type.AnyAsync(t => t.OtherDocument_TypeID == request.TypeId && (t.IsActive ?? 1) == 1))
            return (null, "Choose a document type.");

        // The vessel the document is about: the selected vessel, else -2 (office, no vessel).
        var vesselId = request.VesselId is > 0 ? request.VesselId.Value : SqlDb.OfficeOnly;
        if (vesselId > 0 && !await VesselExistsAsync(vesselId))
            return (null, "Choose a vessel that exists.");
        if (folderId != null && (await FolderAsync(folderId.Value, user))?.VesselID is > 0 and var folderVessel && folderVessel != vesselId)
            return (null, "That folder belongs to another vessel.");

        // Office only (never synced), unless the user chose to send it to the vessel: then the row-sync triggers
        // carry it to that vessel with the next sync, and its files are downloaded on board when first opened.
        var officeOnly = request.SendToVessel ? 0 : 1;
        if (request.SendToVessel)
        {
            if (vesselId <= 0) return (null, "Choose a vessel to send the document to.");
            if (folderId != null && (await FolderAsync(folderId.Value, user))?.VesselID == SqlDb.OfficeOnly)
                return (null, "A document sent to the vessel cannot go into an office-only folder.");
        }

        if (await _dbContext.OtherDocumentMaster.AnyAsync(d => (d.IsDeleted ?? 0) != 1
                && d.OtherDocument_TypeID == request.TypeId && d.DocumentNumber == number
                && d.VesselID == vesselId && d.IsForOfficeOnly == officeOnly))
            return (null, $"A document numbered {number} of this type is already there.");

        var maxBytes = await MaxFileBytesAsync();
        var files = request.Files.Where(f => f.Bytes.Length > 0).ToList();
        if (files.Any(f => f.Bytes.Length > maxBytes)) return (null, $"Files must be {maxBytes / 1024 / 1024} MB or smaller.");

        var id = await _db.NextIdAsync("OtherDocumentMaster");
        var logId = await _db.NextIdAsync("OtherDocument_Log");
        var blobId = files.Count > 0 ? await _db.NextIdAsync("OtherDocument_Blob", count: files.Count) : 0;

        // Each file goes to the database, a local folder or the cloud, as BlobTableSettings says for OtherDocument_Blob.
        var saved = new List<(long Id, string Name, string Ext, int Size, StoredAttachment Stored)>();
        try
        {
            foreach (var f in files)
            {
                var name = Path.GetFileName(f.FileName);
                var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                var fileId = blobId++;
                saved.Add((fileId, name, ext, f.Bytes.Length,
                    await _storage.SaveAsync(AttachmentStorage.OtherDocumentTable, fileId, Cut(name, 150), f.Bytes)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the files of document {Number}.", number);
            await RemoveSavedFilesAsync(saved);
            return (null, "The files could not be saved: " + ex.Message);
        }

        try
        {
            var now = DateTime.UtcNow;
            _dbContext.OtherDocumentMaster.Add(new OtherDocumentMaster
            {
                OtherDocumentMasterID = id,
                Origin = SqlDb.Office,
                VesselID = vesselId,
                IsForOfficeOnly = officeOnly,
                OtherDocument_TypeID = request.TypeId,
                DocumentNumber = number,
                DocumentTitle = title,
                Status = Cut(request.Status, 50) ?? "Active",
                Reference = Cut(request.Reference, 150),
                Comments = Cut(request.Comments, 500),
                UploadedOn = now,
                UploadedBy = user.UserId,
                IsDeleted = 0,
                OtherDocument_FolderID = folderId,
            });
            foreach (var f in saved)
                _dbContext.OtherDocument_Blob.Add(new OtherDocument_Blob
                {
                    OtherDocument_BlobID = f.Id,
                    OtherDocumentMasterID = id,
                    Origin = SqlDb.Office,
                    VesselID = vesselId,
                    IsForOfficeOnly = officeOnly,
                    Description = Cut(f.Name, 150),
                    FileName = Cut(f.Name, 150),
                    FileType = Cut(f.Ext, 50),
                    OriginalFileType = Cut(f.Ext, 50),
                    BlobSize = f.Size,
                    BlobContents = f.Stored.BlobContents,
                    AddedOn = now,
                    Cloud_ContainerName = f.Stored.Cloud_ContainerName,
                    Cloud_Directory = f.Stored.Cloud_Directory,
                    HQ_Storage_Option = f.Stored.HQ_Storage_Option,
                    HQ_Local_Folder = f.Stored.HQ_Local_Folder,
                    HQ_DatabaseName = f.Stored.HQ_DatabaseName,
                    VSL_Storage_Option = f.Stored.VSL_Storage_Option,
                    VSL_DatabaseName = f.Stored.VSL_DatabaseName,
                    VSL_Local_Folder = f.Stored.VSL_Local_Folder,
                });
            _dbContext.OtherDocument_Log.Add(new OtherDocument_Log
            {
                OtherDocument_LogID = logId,
                Origin = SqlDb.Office,
                VesselID = vesselId,
                OtherDocumentMasterID = id,
                LogDate = DateTime.Now,
                Activity = Cut("New " + number + " Added", 150)!,
                Remarks = Cut($"{number}: {title} ({saved.Count} file(s))", 3000),
                UserId = Cut(user.UserId, 150),
                UserName = Cut(user.UserName, 150),
                UserRank = Cut(user.Role, 50) ?? "",
            });
            await _dbContext.SaveChangesAsync();   // the document, its files and the log row in one transaction
        }
        catch
        {
            await RemoveSavedFilesAsync(saved);
            throw;
        }
        return (id, null);
    }

    /// <summary>Removes files already written to a folder or the cloud when the document could not be saved.</summary>
    private async Task RemoveSavedFilesAsync(IEnumerable<(long Id, string Name, string Ext, int Size, StoredAttachment Stored)> saved)
    {
        foreach (var f in saved)
            await _storage.DeleteAsync(AttachmentStorage.OtherDocumentTable, new AttachmentRef(f.Id, Cut(f.Name, 150), null,
                f.Stored.HQ_Storage_Option, f.Stored.HQ_Local_Folder, f.Stored.Cloud_ContainerName, f.Stored.Cloud_Directory));
    }

    /// <summary>Files a document in a folder (null = take it out of its folder).</summary>
    public async Task<string?> MoveDocumentAsync(long documentId, long? folderId, OfficeUser user)
    {
        var doc = (await ProjectDocumentsAsync(await _dbContext.OtherDocumentMaster.AsNoTracking()
            .Where(d => d.OtherDocumentMasterID == documentId && (d.IsDeleted == null || d.IsDeleted != 1)).ToListAsync(), null, false)).FirstOrDefault();
        if (doc == null) return "Document not found.";
        if (doc.OtherDocument_FolderID != null && await FolderAsync(doc.OtherDocument_FolderID.Value, user) is { CanEdit: false })
            return "You cannot move documents out of its folder.";
        if (folderId != null)
        {
            var target = await FolderAsync(folderId.Value, user);
            if (target == null) return "Folder not found.";
            if (!target.CanEdit) return "You cannot add documents to that folder.";
            if (target.VesselID > 0 && doc.VesselID > 0 && doc.VesselID != target.VesselID)
                return "That folder belongs to another vessel.";
            // A vessel's document in an office-only folder would vanish from the vessel's folders.
            if (!doc.IsForOfficeOnly && target.VesselID == SqlDb.OfficeOnly)
                return "A vessel's document cannot go into an office-only folder.";
        }
        var now = DateTime.UtcNow;
        await _dbContext.OtherDocumentMaster.Where(d => d.OtherDocumentMasterID == documentId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.OtherDocument_FolderID, folderId)
                                      .SetProperty(d => d.ModifiedBy, user.UserId)
                                      .SetProperty(d => d.ModifiedDate, now));
        return null;
    }

    /// <summary>Deletes a document added at the office (office only). Documents from or for a vessel are not deleted here.</summary>
    public async Task<string?> DeleteDocumentAsync(long documentId, OfficeUser user)
    {
        var doc = (await ProjectDocumentsAsync(await _dbContext.OtherDocumentMaster.AsNoTracking()
            .Where(d => d.OtherDocumentMasterID == documentId && (d.IsDeleted == null || d.IsDeleted != 1)).ToListAsync(), null, false)).FirstOrDefault();
        if (doc == null) return "Document not found.";
        if (!doc.IsForOfficeOnly) return "Only documents added at the office can be deleted here.";
        if (doc.OtherDocument_FolderID != null && await FolderAsync(doc.OtherDocument_FolderID.Value, user) is { CanEdit: false })
            return "You cannot delete documents in this folder.";
        var now = DateTime.UtcNow;
        await _dbContext.OtherDocumentMaster.Where(d => d.OtherDocumentMasterID == documentId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.IsDeleted, 1)
                                      .SetProperty(d => d.ModifiedBy, user.UserId)
                                      .SetProperty(d => d.ModifiedDate, now));
        return null;
    }

    public async Task<FileContent?> GetDocumentFileAsync(long blobId)
    {
        var row = await (
                from b in _dbContext.OtherDocument_Blob.AsNoTracking()
                join d in _dbContext.OtherDocumentMaster on b.OtherDocumentMasterID equals d.OtherDocumentMasterID
                where b.OtherDocument_BlobID == blobId && (d.IsDeleted ?? 0) != 1
                select new { b.FileName, b.FileType, b.BlobContents, b.HQ_Storage_Option, b.HQ_Local_Folder, b.Cloud_ContainerName, b.Cloud_Directory })
            .FirstOrDefaultAsync();
        if (row == null) return null;
        // In the database, a local folder or the cloud (BlobTableSettings).
        var bytes = await _storage.ReadAsync(AttachmentStorage.OtherDocumentTable, new AttachmentRef(blobId, row.FileName, row.BlobContents,
            row.HQ_Storage_Option, row.HQ_Local_Folder, row.Cloud_ContainerName, row.Cloud_Directory));
        if (bytes == null) return null;
        var fileName = string.IsNullOrWhiteSpace(row.FileName) ? $"file-{blobId}" : row.FileName;
        // FileType holds a MIME type or an extension; the file name decides when it is not a MIME type.
        var contentType = row.FileType != null && row.FileType.Contains('/') ? row.FileType : DMS.Infrastructure.Services.ContentTypes.For(fileName);
        return new FileContent(bytes, contentType, fileName);
    }

    public Task<List<FileItem>> GetDocumentFilesAsync(long documentId)
        => _dbContext.OtherDocument_Blob.AsNoTracking()
            .Where(b => b.OtherDocumentMasterID == documentId)
            .OrderBy(b => b.OtherDocument_BlobID)
            .Select(b => new FileItem
            {
                Id = b.OtherDocument_BlobID,
                FolderID = b.OtherDocumentMasterID,
                FileName = b.FileName != null && b.FileName != "" ? b.FileName : b.Description ?? "File",
                FileType = b.FileType,
                BlobSize = b.BlobSize,
                UploadedOn = b.AddedOn,
            })
            .ToListAsync();

    /// <summary>True when folderId is ancestorId or lies somewhere below it (deleted folders count, as before).</summary>
    private async Task<bool> IsBelowAsync(long folderId, long ancestorId)
    {
        var parents = await _dbContext.FldFolders.AsNoTracking()
            .Select(f => new { f.OtherDocument_FolderID, f.ParentID })
            .ToDictionaryAsync(f => f.OtherDocument_FolderID, f => f.ParentID);
        long? current = folderId;
        for (var depth = 0; current != null && depth <= 50; depth++)
        {
            if (current == ancestorId) return true;
            if (!parents.TryGetValue(current.Value, out current)) return false;
        }
        return false;
    }

    private Task<bool> NameTakenAsync(long? parentId, string name, string scope, int vesselId, string? owner, long? exceptId)
        => _dbContext.FldFolders.AnyAsync(f => f.IsDeleted == 0 && f.FolderName == name && f.Scope == scope && f.VesselID == vesselId
                                               && f.ParentID == parentId
                                               && f.OwnerUserID == owner
                                               && (exceptId == null || f.OtherDocument_FolderID != exceptId));

    private Task<bool> VesselExistsAsync(int vesselId) => _generalContext.VVessels.AnyAsync(v => v.VesselID == vesselId);

    /// <summary>Largest file allowed on a document (DM_Settings FLD_MaxFileSizeMB, default 25 MB).</summary>
    private async Task<long> MaxFileBytesAsync()
    {
        var mb = int.TryParse(await _db.SettingAsync("DM_Settings", "FLD_MaxFileSizeMB"), out var m) && m > 0 ? m : 25;
        return mb * 1024L * 1024L;
    }

    private static string? Roles(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var list = string.Join(",", csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        return list.Length == 0 ? null : list.Length <= 500 ? list : list[..500];
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
}
