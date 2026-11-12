using System.Globalization;
using System.Text.Json;
using DMS.Data.EF;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Circulars;
using DMS.Data.EF.Models.Features;
using DMS.Data.EF.Models.Manuals;
using DMS.Data.EF.Models.OtherDocument;
using DMS.Application.DTO.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DMS.Application;

/// <summary>
/// Ship -> office: POST api/Sync/Upload, the one call for everything made or changed on board. Each record is stored on
/// its own (a refused one never stops the others) under the key it has on board when that key is free here:
///   DM_Manual_UserAck, CIR_UserAck   acknowledgements: one per user and manual version / circular, crew matched by user ID
///   OtherDocumentMaster              documents added on board (matched by type and number); for one already here, its folder
///   OtherDocument_Blob               their files (contents as file parts)
///   SUG_*, DMS_InboxRead, OtherDocument_Folder, OtherDocument_Log
///                                    stored as they are, column by column (PushableTables)
/// The answer says per record whether the office holds it, and for acknowledgements and documents gives the office copy
/// (and its key when that differs) so the ship can replace its own.
/// </summary>
public partial class SyncService
{
    private const string ManualAckTable = "DM_Manual_UserAck";
    private const string CircularAckTable = "CIR_UserAck";
    private const string DocumentTable = "OtherDocumentMaster";
    private const string DocumentFileTable = "OtherDocument_Blob";

    /// <summary>The tables a vessel may send as they are, with their key column.</summary>
    public static readonly IReadOnlyDictionary<string, string> PushableTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DMS_InboxRead"] = "DMS_InboxReadID",
        ["SUG_Master"] = "SUG_MasterID",
        ["SUG_Log"] = "SUG_LogID",
        ["SUG_Blob"] = "SUG_BlobID",
        ["OtherDocument_Folder"] = "OtherDocument_FolderID",
        ["OtherDocument_Log"] = "OtherDocument_LogID",
    };

    /// <summary>Records the office owns once stored: a repeat from the ship never overwrites the office's changes.</summary>
    private static readonly HashSet<string> InsertOnlyTables = new(StringComparer.OrdinalIgnoreCase)
        { "DMS_InboxRead", "SUG_Master", "SUG_Log", "SUG_Blob", "OtherDocument_Log" };

    /// <summary>The EF entity of each pushable table (mapped in GeneralContext or OtherDocumentsContext).</summary>
    private static readonly IReadOnlyDictionary<string, Type> PushableEntities = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["DMS_InboxRead"] = typeof(DmsInboxRead),
        ["SUG_Master"] = typeof(SugMaster),
        ["SUG_Log"] = typeof(SugLog),
        ["SUG_Blob"] = typeof(SugBlob),
        ["OtherDocument_Folder"] = typeof(OtherDocument_Folder),
        ["OtherDocument_Log"] = typeof(OtherDocument_Log),
    };

    /// <summary>The order records are stored in: parents before children; anything else after these.</summary>
    private static readonly string[] UploadOrder =
    {
        "OtherDocument_Folder", DocumentTable, DocumentFileTable, "OtherDocument_Log",
        "SUG_Master", "SUG_Log", "SUG_Blob", "DMS_InboxRead", ManualAckTable, CircularAckTable,
    };

    /// <summary>
    /// Stores what a vessel sent. <paramref name="files"/>: the file parts by name (SyncUploadRow.FileRef).
    /// </summary>
    public async Task<SyncUploadResult> UploadAsync(int vesselId, SyncUploadRequest request, IReadOnlyDictionary<string, byte[]> files)
    {
        var started = DateTime.UtcNow;
        var result = new SyncUploadResult();
        // A record stored here under another key than on board: its children in the same call follow it.
        var officeKeys = new Dictionary<(string Table, long Key), long>();

        var rows = request.Rows
            .Select((row, i) => (row, i))
            .OrderBy(x => Array.FindIndex(UploadOrder, t => string.Equals(t, x.row.TableName, StringComparison.OrdinalIgnoreCase)) is var r and >= 0 ? r : UploadOrder.Length)
            .ThenBy(x => x.i)
            .Select(x => x.row);

        foreach (var row in rows)
        {
            var outcome = new SyncUploadRowResult { TableName = row.TableName, Key = row.Key };
            result.Results.Add(outcome);
            try
            {
                var values = Values(row.Row);
                var bytes = row.FileRef != null && files.TryGetValue(row.FileRef, out var b) ? b : null;
                var remove = string.Equals(row.Action, "D", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(row.TableName, ManualAckTable, StringComparison.OrdinalIgnoreCase))
                    await UploadManualAckAsync(vesselId, row, values, remove, outcome);
                else if (string.Equals(row.TableName, CircularAckTable, StringComparison.OrdinalIgnoreCase))
                    await UploadCircularAckAsync(vesselId, row, values, remove, outcome);
                else if (string.Equals(row.TableName, DocumentTable, StringComparison.OrdinalIgnoreCase))
                    await UploadDocumentAsync(vesselId, row, values, remove, outcome, officeKeys);
                else if (string.Equals(row.TableName, DocumentFileTable, StringComparison.OrdinalIgnoreCase))
                    await UploadDocumentFileAsync(vesselId, row, values, bytes, remove, outcome, officeKeys);
                else
                {
                    outcome.Error = await UploadRowAsync(vesselId, row, values, bytes, remove, officeKeys);
                    outcome.Stored = outcome.Error == null;
                }
            }
            catch (Exception ex)
            {
                outcome.Stored = false;
                outcome.Error = (ex as DbUpdateException)?.InnerException?.Message ?? ex.Message;
            }
            finally
            {
                // A refused record must not be saved again with the next one.
                _dms.ChangeTracker.Clear();
                _cir.ChangeTracker.Clear();
                _general.ChangeTracker.Clear();
                _other.ChangeTracker.Clear();
            }
            if (outcome.Stored && outcome.OfficeKey is { } officeKey && officeKey != row.Key)
                officeKeys[(row.TableName.ToUpperInvariant(), row.Key)] = officeKey;
        }

        var stored = result.Results.Count(r => r.Stored);
        var firstError = result.Results.FirstOrDefault(r => r.Error != null)?.Error;
        await RecordCallAsync(vesselId, "Up", started, stored, firstError);
        return result;
    }

    // ---- Acknowledgements ------------------------------------------------------------------------------------------

    /// <summary>A manual read on board: one per user and manual version at the office (a repeat is the one held).</summary>
    private async Task UploadManualAckAsync(int vesselId, SyncUploadRow row, Dictionary<string, JsonElement> values, bool remove,
        SyncUploadRowResult outcome)
    {
        if (remove) { outcome.Stored = true; return; } // the office keeps acknowledgements it holds

        var versionId = Long(values, "DM_ManualVersionID");
        var userId = Str(values, "UserID")?.Trim();
        if (versionId == null || string.IsNullOrEmpty(userId)) { outcome.Error = "The acknowledgement has no manual version or user."; return; }

        var version = await _dms.DmManualVersions.AsNoTracking()
            .Where(v => v.DM_ManualVersionID == versionId)
            .Select(v => new { v.DM_ManualID, v.Version })
            .FirstOrDefaultAsync();
        if (version == null) { outcome.Error = $"Manual version {versionId} is not known at the office."; return; }

        var held = (await _dms.DmManualUserAcks.AsNoTracking()
                .Where(a => a.VesselID == vesselId && a.DM_ManualVersionID == versionId)
                .ToListAsync())
            .FirstOrDefault(a => string.Equals(a.UserId.Trim(), userId, StringComparison.OrdinalIgnoreCase));
        if (held == null)
        {
            held = new DmManualUserAck
            {
                // The ID given on board (Proc_NextID, this vessel's range) when free here, else a new one in that range.
                DmManualUserAckId = await _dms.KeepOrNextIdAsync<DmManualUserAck>(row.Key, TableIdExtensions.OriginVessel, vesselId),
                Origin = TableIdExtensions.OriginVessel,
                VesselID = vesselId,
                DM_ManualID = version.DM_ManualID,
                DM_ManualVersionID = versionId.Value,
                Version = LeadingNumber(version.Version) ?? (int)(Long(values, "Version") ?? 0),
                UserId = userId,
                UserRank = Truncate(Str(values, "UserRank"), 50),
                UserName = Truncate(Str(values, "UserName"), 150),
                DateRead = ToUtc(Date(values, "DateRead") ?? DateTime.UtcNow),
                Comments = Truncate(Str(values, "Comments"), 500),
                IsActive = 1,
            };
            _dms.DmManualUserAcks.Add(held);
            await _dms.SaveChangesAsync();
        }
        outcome.Stored = true;
        outcome.OfficeKey = held.DmManualUserAckId;
        outcome.OfficeRow = RowOf(_dms, held);
    }

    /// <summary>A circular read on board: one per crew member and circular at the office; the crew member by user ID.</summary>
    private async Task UploadCircularAckAsync(int vesselId, SyncUploadRow row, Dictionary<string, JsonElement> values, bool remove,
        SyncUploadRowResult outcome)
    {
        if (remove) { outcome.Stored = true; return; }

        var circularId = Long(values, "CIR_MasterID");
        var userId = Str(values, "UserID")?.Trim();
        if (circularId == null || string.IsNullOrEmpty(userId)) { outcome.Error = "The acknowledgement has no circular or user."; return; }

        var crewId = (await _general.VslStaffs.AsNoTracking()
                .Where(s => s.VesselID == vesselId)
                .Select(s => new { s.UserId, s.VslStaffId })
                .ToListAsync())
            .FirstOrDefault(s => string.Equals(s.UserId.Trim(), userId, StringComparison.OrdinalIgnoreCase))?.VslStaffId;
        if (crewId == null) { outcome.Error = $"Crew member '{userId}' is not in the office's crew list for this vessel."; return; }
        if (!await _cir.CIR_Master.AsNoTracking().AnyAsync(c => c.CIR_MasterID == circularId))
        {
            outcome.Error = $"Circular {circularId} is not known at the office.";
            return;
        }

        var held = await _cir.CIR_UserAck.AsNoTracking()
            .FirstOrDefaultAsync(a => a.VesselID == vesselId && a.CIR_MasterID == circularId && a.Fleet_CrewID == crewId);
        if (held == null)
        {
            held = new CIR_UserAck
            {
                CIR_UserAckID = await _cir.KeepOrNextIdAsync<CIR_UserAck>(row.Key, TableIdExtensions.OriginVessel, vesselId),
                Origin = TableIdExtensions.OriginVessel,
                VesselID = vesselId,
                CIR_MasterID = circularId,
                Fleet_CrewID = crewId,
                DateRead = ToUtc(Date(values, "DateRead") ?? DateTime.UtcNow),
                Comments = Truncate(Str(values, "Comments"), 500),
                IsActive = 1,
            };
            _cir.CIR_UserAck.Add(held);
            await _cir.SaveChangesAsync();
        }
        outcome.Stored = true;
        outcome.OfficeKey = held.CIR_UserAckID;
        outcome.OfficeRow = RowOf(_cir, held);
    }

    // ---- Other Documents ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A document added on board (stored once: the same type and number from this vessel is the one held), or one this
    /// vessel already has at the office that changed on board - then only its folder is taken over.
    /// </summary>
    private async Task UploadDocumentAsync(int vesselId, SyncUploadRow row, Dictionary<string, JsonElement> values, bool remove,
        SyncUploadRowResult outcome, Dictionary<(string Table, long Key), long> officeKeys)
    {
        if (remove) { outcome.Stored = true; return; } // removed on board: the office keeps its copy

        var number = (Str(values, "DocumentNumber") ?? "").Trim();
        var title = (Str(values, "DocumentTitle") ?? "").Trim();
        var typeId = Long(values, "OtherDocument_TypeID");
        var folderId = OfficeKeyOf(officeKeys, "OtherDocument_Folder", Long(values, "OtherDocument_FolderID"));

        // Held already: under this key for this vessel, or (sent before, answer lost) the same type and number.
        var held = await _other.OtherDocumentMaster.FirstOrDefaultAsync(m => m.OtherDocumentMasterID == row.Key && m.VesselID == vesselId)
                   ?? await _other.OtherDocumentMaster.FirstOrDefaultAsync(m =>
                          m.VesselID == vesselId && m.Origin == TableIdExtensions.OriginVessel && (m.IsDeleted ?? 0) != 1
                          && m.OtherDocument_TypeID == typeId && m.DocumentNumber == number && number != "");
        if (held != null)
        {
            if (held.IsForOfficeOnly == 0 && values.ContainsKey("OtherDocument_FolderID") && held.OtherDocument_FolderID != folderId)
            {
                held.OtherDocument_FolderID = folderId; // filed in another folder on board
                await _other.SaveChangesAsync();
            }
            outcome.Stored = true;
            outcome.OfficeKey = held.OtherDocumentMasterID;
            outcome.OfficeRow = RowOf(_other, held);
            return;
        }

        if (number.Length == 0 || title.Length == 0) { outcome.Error = "The document has no number or title."; return; }
        if (typeId == null || !await _other.OtherDocument_Type.AsNoTracking().AnyAsync(t => t.OtherDocument_TypeID == typeId))
        {
            outcome.Error = $"Document type {typeId} is not known at the office.";
            return;
        }

        var uploadedOn = Date(values, "UploadedOn") is { } on ? ToUtc(on) : DateTime.UtcNow;
        var by = Str(values, "UploadedBy")?.Trim();
        var doc = new OtherDocumentMaster
        {
            OtherDocumentMasterID = await _other.KeepOrNextIdAsync<OtherDocumentMaster>(row.Key, TableIdExtensions.OriginVessel, vesselId),
            Origin = TableIdExtensions.OriginVessel,
            VesselID = vesselId,
            OtherDocument_TypeID = typeId,
            DocumentNumber = Truncate(number, 50)!,
            DocumentTitle = Truncate(title, 500)!,
            Status = Truncate(Str(values, "Status")?.Trim(), 50),
            Reference = Truncate(Str(values, "Reference")?.Trim(), 150),
            Comments = Truncate(Str(values, "Comments")?.Trim(), 500),
            UploadedOn = uploadedOn,
            UploadedBy = Truncate(by, 150),
            IsDeleted = 0,
            ModifiedBy = Truncate(by, 50),
            ModifiedDate = uploadedOn,
            OtherDocument_FolderID = folderId,
        };
        _other.OtherDocumentMaster.Add(doc);
        // No log row here: the vessel wrote its own "Added" log on board, and sends it too.
        await _other.SaveChangesAsync();
        outcome.Stored = true;
        outcome.OfficeKey = doc.OtherDocumentMasterID;
        outcome.OfficeRow = RowOf(_other, doc);
    }

    /// <summary>A file of a document, with its contents when the vessel sent them (new on board).</summary>
    private async Task UploadDocumentFileAsync(int vesselId, SyncUploadRow row, Dictionary<string, JsonElement> values, byte[]? bytes,
        bool remove, SyncUploadRowResult outcome, Dictionary<(string Table, long Key), long> officeKeys)
    {
        if (remove) { outcome.Stored = true; return; }

        var masterId = OfficeKeyOf(officeKeys, DocumentTable, Long(values, "OtherDocumentMasterID"));
        var master = masterId == null ? null : await _other.OtherDocumentMaster.AsNoTracking()
            .Where(m => m.OtherDocumentMasterID == masterId)
            .Select(m => new { m.VesselID, m.IsDeleted, m.IsForOfficeOnly })
            .FirstOrDefaultAsync();
        if (master == null)
        {
            if (bytes == null) { outcome.Stored = true; return; } // a change to a file the office never had: nothing to do
            outcome.Error = $"Document {masterId} is not at the office yet; the file goes again after it.";
            return;
        }
        if (master.VesselID != vesselId) { outcome.Error = $"Document {masterId} belongs to another vessel at the office."; return; }

        var name = Truncate(Path.GetFileName(Str(values, "FileName") ?? ""), 150);
        var size = bytes?.Length ?? (int)(Long(values, "BlobSize") ?? 0);
        var held = await _other.OtherDocument_Blob.AsNoTracking()
                       .FirstOrDefaultAsync(f => f.OtherDocument_BlobID == row.Key && f.OtherDocumentMasterID == masterId)
                   ?? await _other.OtherDocument_Blob.AsNoTracking()
                       .FirstOrDefaultAsync(f => f.OtherDocumentMasterID == masterId && f.FileName == name && f.BlobSize == size);
        if (held != null || (master.IsDeleted ?? 0) == 1 || bytes == null || bytes.Length == 0)
        {
            // Held already; or nothing to store (document deleted at the office, or no contents sent): stops waiting.
            outcome.Stored = true;
            if (held != null)
            {
                outcome.OfficeKey = held.OtherDocument_BlobID;
                outcome.OfficeRow = RowOf(_other, held, "BlobContents");
            }
            return;
        }

        var fileId = await _other.KeepOrNextIdAsync<OtherDocument_Blob>(row.Key, TableIdExtensions.OriginVessel, vesselId);
        // Kept at the office in the database, a local folder or the cloud, as BlobTableSettings says for OtherDocument_Blob.
        var stored = await _storage.SaveAsync(AttachmentStorage.OtherDocumentTable, fileId, name, bytes);
        var file = new OtherDocument_Blob
        {
            OtherDocument_BlobID = fileId,
            OtherDocumentMasterID = masterId!.Value,
            Origin = TableIdExtensions.OriginVessel,
            VesselID = vesselId,
            IsForOfficeOnly = master.IsForOfficeOnly,
            Description = Truncate(Str(values, "Description") ?? name, 150),
            FileName = name,
            FileType = Truncate(Str(values, "FileType"), 50),
            OriginalFileType = Truncate(Str(values, "OriginalFileType"), 50),
            BlobSize = bytes.Length,
            BlobContents = stored.BlobContents,
            AddedOn = Date(values, "AddedOn") is { } added ? ToUtc(added) : DateTime.UtcNow,
            Cloud_ContainerName = stored.Cloud_ContainerName,
            Cloud_Directory = stored.Cloud_Directory,
            HQ_Storage_Option = stored.HQ_Storage_Option,
            HQ_Local_Folder = stored.HQ_Local_Folder,
            HQ_DatabaseName = stored.HQ_DatabaseName,
            VSL_Storage_Option = stored.VSL_Storage_Option,
            VSL_Local_Folder = stored.VSL_Local_Folder,
            VSL_DatabaseName = stored.VSL_DatabaseName,
        };
        _other.OtherDocument_Blob.Add(file);
        await _storage.SaveOrDiscardAsync(() => _other.SaveChangesAsync()); // a failed save leaves no stored file behind
        outcome.Stored = true;
        outcome.OfficeKey = fileId;
        outcome.OfficeRow = RowOf(_other, file, "BlobContents");
    }

    // ---- Records stored as they are ------------------------------------------------------------------------------

    /// <summary>One record of a PushableTables table; null when stored (now or before), else why not.</summary>
    private async Task<string?> UploadRowAsync(int vesselId, SyncUploadRow row, Dictionary<string, JsonElement> values, byte[]? bytes,
        bool remove, Dictionary<(string Table, long Key), long> officeKeys)
    {
        if (!PushableTables.TryGetValue(row.TableName, out var keyColumn)) return $"{row.TableName} cannot be sent from a vessel.";
        var table = PushableTables.Keys.First(k => string.Equals(k, row.TableName, StringComparison.OrdinalIgnoreCase));
        // The context that maps the table: suggestions and inbox in GeneralContext, folders and the document log in
        // OtherDocumentsContext.
        DbContext db = _general.Model.FindEntityType(PushableEntities[table]) != null ? _general : _other;
        var entityType = db.Model.FindEntityType(PushableEntities[table])
                         ?? throw new InvalidOperationException($"{table} is not mapped in GeneralContext or OtherDocumentsContext.");

        // Removed on board: removed here too when it is this vessel's record. Records the office owns once stored
        // (InsertOnlyTables) stay, and one the office never had needs nothing.
        if (remove)
        {
            if (InsertOnlyTables.Contains(table)) return null;
            var gone = await db.FindAsync(entityType.ClrType, row.Key);
            if (gone == null) return null;
            var goneEntry = db.Entry(gone);
            if ((goneEntry.Property("Origin").CurrentValue as string) != TableIdExtensions.OriginVessel
                || (int)goneEntry.Property("VesselID").CurrentValue! != vesselId)
                return $"{table} {row.Key} is not this vessel's record at the office.";
            goneEntry.State = EntityState.Deleted;
            await db.SaveChangesAsync();
            return null;
        }

        // Column name -> property, from the EF model.
        var storeObject = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var properties = entityType.GetProperties()
            .Select(p => (Column: p.GetColumnName(storeObject), Property: p))
            .Where(x => x.Column != null)
            .ToDictionary(x => x.Column!, x => x.Property, StringComparer.OrdinalIgnoreCase);

        // The values, converted to the property types. Origin and VesselID are always the calling vessel's.
        var record = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, element) in values)
        {
            if (!properties.TryGetValue(name, out var property)) continue; // columns only the ship has
            record[property.Name] = FromJson(element, property.ClrType);
        }
        if (bytes != null && properties.TryGetValue("BlobContents", out var contents))
            record[contents.Name] = bytes;
        record[properties[keyColumn].Name] = row.Key;
        record[properties["Origin"].Name] = TableIdExtensions.OriginVessel;
        record[properties["VesselID"].Name] = vesselId;
        // The document log of a document the office holds under another key follows it.
        if (string.Equals(table, "OtherDocument_Log", StringComparison.OrdinalIgnoreCase)
            && properties.TryGetValue("OtherDocumentMasterID", out var docColumn)
            && record.GetValueOrDefault(docColumn.Name) is long logDoc)
            record[docColumn.Name] = OfficeKeyOf(officeKeys, DocumentTable, logDoc);

        var existing = await db.FindAsync(entityType.ClrType, row.Key);
        if (existing != null)
        {
            var entry = db.Entry(existing);
            if ((entry.Property("Origin").CurrentValue as string) != TableIdExtensions.OriginVessel
                || (int)entry.Property("VesselID").CurrentValue! != vesselId)
                return $"{table} {row.Key} is already used at the office for another record.";
            if (InsertOnlyTables.Contains(table)) return null; // stored before
            foreach (var (name, value) in record)
                if (!entry.Property(name).Metadata.IsPrimaryKey())
                    entry.Property(name).CurrentValue = value;
            await db.SaveChangesAsync();
            return null;
        }

        var error = await CheckParentAsync(table, record, vesselId);
        if (error != null) return error;

        var entity = Activator.CreateInstance(entityType.ClrType)!;
        var added = db.Entry(entity);
        foreach (var (name, value) in record)
            added.Property(name).CurrentValue = value;
        added.State = EntityState.Added;

        // An answer on board to "needs more information" puts the suggestion back under review.
        if (entity is SugLog log && string.Equals(log.Status, "Answered", StringComparison.OrdinalIgnoreCase))
        {
            var suggestion = await _general.SugMasters.FirstOrDefaultAsync(m => m.SUG_MasterID == log.SUG_MasterID && m.Status == "NeedsInfo");
            if (suggestion != null)
            {
                suggestion.Status = "UnderReview";
                suggestion.ModifiedOn = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(); // the row (and the suggestion's status) in one transaction
        return null;
    }

    public async Task<(byte[] Bytes, string? FileName)?> GetFeatureFileAsync(int vesselId, string table, long blobId)
    {
        if (table != "SUG_Blob") return null;
        var row = await _general.SugBlobs.AsNoTracking()
            .Where(b => b.SUG_BlobID == blobId)
            .Select(b => new { b.VesselID, b.FileName, b.BlobContents })
            .FirstOrDefaultAsync();
        if (row == null || (row.VesselID != vesselId && row.VesselID != -1)) return null;
        // Suggestion files are only kept in the database: no contents there means there is no file (204).
        return (row.BlobContents is { Length: > 0 } ? row.BlobContents : Array.Empty<byte>(), row.FileName);
    }

    /// <summary>A child record needs its parent at the office, and the parent must be this vessel's (or for all vessels).</summary>
    private async Task<string?> CheckParentAsync(string table, Dictionary<string, object?> values, int vesselId)
    {
        (string Table, string Column)? parent = table switch
        {
            "SUG_Log" or "SUG_Blob" => ("SUG_Master", "SUG_MasterID"),
            "DMS_InboxRead" => ("DMS_Inbox", "DMS_InboxID"),
            "OtherDocument_Log" => ("OtherDocumentMaster", "OtherDocumentMasterID"),
            _ => null,
        };
        if (parent == null) return null;
        if (values.GetValueOrDefault(parent.Value.Column) is not long id) return $"{table} has no {parent.Value.Column}.";

        int? ownerVessel = parent.Value.Table == "OtherDocumentMaster"
            ? await _other.OtherDocumentMaster.AsNoTracking().Where(m => m.OtherDocumentMasterID == id && (m.IsDeleted ?? 0) != 1).Select(m => (int?)m.VesselID).FirstOrDefaultAsync()
            : parent.Value.Table == "SUG_Master"
            ? await _general.SugMasters.AsNoTracking().Where(m => m.SUG_MasterID == id).Select(m => (int?)m.VesselID).FirstOrDefaultAsync()
            : await _general.DmsInbox.AsNoTracking().Where(i => i.DMS_InboxID == id).Select(i => (int?)i.VesselID).FirstOrDefaultAsync();
        if (ownerVessel == null) return $"{parent.Value.Table} {id} is not at the office yet.";
        if (ownerVessel != vesselId && ownerVessel != -1) return $"{parent.Value.Table} {id} belongs to another vessel.";
        return null;
    }

    // ---- Values ------------------------------------------------------------------------------------------------------

    /// <summary>The key a parent is held under at the office (another one when it was stored under a new key in this call).</summary>
    private static long? OfficeKeyOf(Dictionary<(string Table, long Key), long> officeKeys, string table, long? key)
        => key != null && officeKeys.TryGetValue((table.ToUpperInvariant(), key.Value), out var officeKey) ? officeKey : key;

    /// <summary>The record as sent (JSON values, by column name; any case).</summary>
    private static Dictionary<string, JsonElement> Values(Dictionary<string, object?> row)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in row)
            values[name] = value is JsonElement e ? e : JsonSerializer.SerializeToElement(value);
        return values;
    }

    /// <summary>A record as the office holds it, by column name (what the ship writes over its own), without the given columns.</summary>
    private static Dictionary<string, object?> RowOf(DbContext db, object entity, params string[] leaveOut)
    {
        var entityType = db.Model.FindEntityType(entity.GetType())!;
        var storeObject = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var entry = db.Entry(entity);
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in entityType.GetProperties())
        {
            var column = p.GetColumnName(storeObject);
            if (column == null || leaveOut.Contains(column, StringComparer.OrdinalIgnoreCase)) continue;
            row[column] = entry.Property(p.Name).CurrentValue;
        }
        return row;
    }

    private static string? Str(Dictionary<string, JsonElement> values, string column)
        => values.TryGetValue(column, out var e) ? FromJson(e, typeof(string)) as string : null;

    private static long? Long(Dictionary<string, JsonElement> values, string column)
        => values.TryGetValue(column, out var e) ? FromJson(e, typeof(long?)) as long? : null;

    private static DateTime? Date(Dictionary<string, JsonElement> values, string column)
        => values.TryGetValue(column, out var e) ? FromJson(e, typeof(DateTime?)) as DateTime? : null;

    /// <summary>A JSON value from the ship as the property's CLR type.</summary>
    private static object? FromJson(JsonElement e, Type type)
    {
        if (e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(long)) return e.ValueKind == JsonValueKind.String ? long.Parse(e.GetString()!, CultureInfo.InvariantCulture) : e.GetInt64();
        if (t == typeof(int) || t == typeof(short) || t == typeof(byte))
        {
            var number = e.ValueKind is JsonValueKind.True or JsonValueKind.False ? (e.GetBoolean() ? 1 : 0)
                : e.ValueKind == JsonValueKind.String ? int.Parse(e.GetString()!, CultureInfo.InvariantCulture) : e.GetInt32();
            return Convert.ChangeType(number, t, CultureInfo.InvariantCulture);
        }
        if (t == typeof(bool)) return e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : e.GetInt32() != 0;
        if (t == typeof(decimal)) return e.GetDecimal();
        if (t == typeof(double)) return e.GetDouble();
        if (t == typeof(float)) return e.GetSingle();
        if (t == typeof(DateTime)) return DateTime.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (t == typeof(byte[])) return e.GetBytesFromBase64();
        return e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
    }
}
