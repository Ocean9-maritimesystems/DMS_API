using System.Globalization;
using System.Text.Json;
using DMS.Application.DTO.Sync;
using DMS.Data.EF.Models.Features;
using DMS.Data.EF.Models.OtherDocument;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DMS.Application;

/// <summary>
/// Ship -> office push of the new feature tables (Database/012_NewFeatures.sql), row by row under the same key.
/// The ship sends a record as it is; the office inserts it, or updates it when it is already this vessel's record.
/// </summary>
public partial class SyncService
{
    /// <summary>The tables a vessel may push, with their key column.</summary>
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

    /// <summary>The EF entity of each pushable table (all mapped in GeneralContext).</summary>
    private static readonly IReadOnlyDictionary<string, Type> PushableEntities = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["DMS_InboxRead"] = typeof(DmsInboxRead),
        ["SUG_Master"] = typeof(SugMaster),
        ["SUG_Log"] = typeof(SugLog),
        ["SUG_Blob"] = typeof(SugBlob),
        ["OtherDocument_Folder"] = typeof(OtherDocument_Folder),
        ["OtherDocument_Log"] = typeof(OtherDocument_Log),
    };

    public async Task<SyncPushRowsResult> PushRowsAsync(int vesselId, SyncPushRowsRequest request)
    {
        var started = DateTime.UtcNow;
        var result = new SyncPushRowsResult();

        // Parents before children, so a suggestion is stored before its log.
        var order = PushableTables.Keys.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i, StringComparer.OrdinalIgnoreCase);
        foreach (var row in request.Rows.OrderBy(r => order.TryGetValue(r.TableName, out var i) ? i : int.MaxValue))
        {
            var outcome = new SyncPushRowResult { TableName = row.TableName, Key = row.Key };
            result.Results.Add(outcome);
            try
            {
                outcome.Error = await PushRowAsync(vesselId, row);
                outcome.Stored = outcome.Error == null;
            }
            catch (Exception ex)
            {
                outcome.Error = (ex as DbUpdateException)?.InnerException?.Message ?? ex.Message;
            }
            finally
            {
                _general.ChangeTracker.Clear(); // a failed row must not be saved again with the next one
            }
        }

        var stored = result.Results.Count(r => r.Stored);
        var firstError = result.Results.FirstOrDefault(r => r.Error != null)?.Error;
        await RecordCallAsync(vesselId, "Up", started, stored, firstError);
        return result;
    }

    /// <summary>Pseudo table: an Other Document of this vessel moved to another folder on board (Row: OtherDocument_FolderID).</summary>
    public const string DocumentFolderRow = "DocumentFolder";

    private async Task<string?> PushRowAsync(int vesselId, SyncPushRow row)
    {
        if (string.Equals(row.TableName, DocumentFolderRow, StringComparison.OrdinalIgnoreCase))
        {
            long? folder = row.Row.TryGetValue("OtherDocument_FolderID", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : null;
            var updated = await _other.OtherDocumentMaster
                .Where(d => d.OtherDocumentMasterID == row.Key && d.VesselID == vesselId && d.IsForOfficeOnly == 0)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.OtherDocument_FolderID, folder));
            return updated == 1 ? null : $"Document {row.Key} is not this vessel's document at the office.";
        }

        if (!PushableTables.TryGetValue(row.TableName, out var keyColumn)) return $"{row.TableName} cannot be sent from a vessel.";
        var table = PushableTables.Keys.First(k => string.Equals(k, row.TableName, StringComparison.OrdinalIgnoreCase));
        var entityType = _general.Model.FindEntityType(PushableEntities[table])
                         ?? throw new InvalidOperationException($"{table} is not mapped in GeneralContext.");

        // Removed on board: removed here too when it is this vessel's record. Records the office owns once stored
        // (InsertOnlyTables) stay, and one the office never had needs nothing.
        if (string.Equals(row.Action, "D", StringComparison.OrdinalIgnoreCase))
        {
            if (InsertOnlyTables.Contains(table)) return null;
            var gone = await _general.FindAsync(entityType.ClrType, row.Key);
            if (gone == null) return null;
            var goneEntry = _general.Entry(gone);
            if ((goneEntry.Property("Origin").CurrentValue as string) != "VSL" || (int)goneEntry.Property("VesselID").CurrentValue! != vesselId)
                return $"{table} {row.Key} is not this vessel's record at the office.";
            goneEntry.State = EntityState.Deleted;
            await _general.SaveChangesAsync();
            return null;
        }

        // Column name -> property, from the EF model.
        var storeObject = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var properties = entityType.GetProperties()
            .Select(p => (Column: p.GetColumnName(storeObject), Property: p))
            .Where(x => x.Column != null)
            .ToDictionary(x => x.Column!, x => x.Property, StringComparer.OrdinalIgnoreCase);

        // The values, converted to the property types. Origin and VesselID are always the calling vessel's.
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, element) in row.Row)
        {
            if (!properties.TryGetValue(name, out var property)) continue; // columns only the ship has
            values[property.Name] = FromJson(element, property.ClrType);
        }
        values[properties[keyColumn].Name] = row.Key;
        values[properties["Origin"].Name] = "VSL";
        values[properties["VesselID"].Name] = vesselId;


        var existing = await _general.FindAsync(entityType.ClrType, row.Key);
        if (existing != null)
        {
            var entry = _general.Entry(existing);
            if ((entry.Property("Origin").CurrentValue as string) != "VSL" || (int)entry.Property("VesselID").CurrentValue! != vesselId)
                return $"{table} {row.Key} is already used at the office for another record.";
            if (InsertOnlyTables.Contains(table)) return null; // stored before
            foreach (var (name, value) in values)
                if (!entry.Property(name).Metadata.IsPrimaryKey())
                    entry.Property(name).CurrentValue = value;
            await _general.SaveChangesAsync();
            return null;
        }

        var error = await CheckParentAsync(table, values, vesselId);
        if (error != null) return error;

        var entity = Activator.CreateInstance(entityType.ClrType)!;
        var added = _general.Entry(entity);
        foreach (var (name, value) in values)
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

        await _general.SaveChangesAsync(); // the row (and the suggestion's status) in one transaction
        return null;
    }

    public async Task<(byte[] Bytes, string? FileName)?> GetFeatureFileAsync(int vesselId, string table, long blobId)
    {
        if (table != "SUG_Blob") return null;
        var row = await _general.SugBlobs.AsNoTracking()
            .Where(b => b.SUG_BlobID == blobId)
            .Select(b => new { b.VesselID, b.FileName, b.BlobContents })
            .FirstOrDefaultAsync();
        if (row?.BlobContents == null || (row.VesselID != vesselId && row.VesselID != -1)) return null;
        return (row.BlobContents, row.FileName);
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
