using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using DMS.Application.DTO.Sync;
using DMS.Application.Interface;
using DMS.Data.EF;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Circulars;
using DMS.Data.EF.Models.Features;
using DMS.Data.EF.Models.Manuals;
using DMS.Data.EF.Models.OtherDocument;
using Microsoft.EntityFrameworkCore;

namespace DMS.Application;

/// <summary>
/// Office side of the vessel sync (api/Sync/...). Needs the tables from Database/005_VesselSync.sql.
///
/// What a vessel gets:
///   Manuals    every manual listed for the vessel in DM_Manual_ToAck_Vsl (VesselID = the vessel, or -1 for all
///              vessels) that has a released version (IsActive = 1), plus the manuals above it in the tree.
///   Circulars  every circular and alert listed for the vessel in CIR_ToAck_Vsl (vessel or -1), except Temp and Draft.
///   Crew       Vsl_Staff for the vessel. Categories: all manual and circular categories.
///
/// How "already received" is tracked (Sync_VesselItem): each item has a version key, a hash of everything the
/// vessel shows for it (text, version, attachments, ranks that must acknowledge ...). An item is sent while the key
/// differs from the key the vessel confirmed. A pull marks what it sends as pending; the vessel's next pull
/// (its "since" = ServerTimeUtc of the pull it applied) confirms them. A lost response is simply sent again.
/// </summary>
public partial class SyncService : ISyncService
{
    /// <summary>A first sync is sent in batches so one response never gets too big for a satellite link.</summary>
    private const int MaxItemsPerPull = 200;
    private const long MaxTextCharsPerPull = 4_000_000;

    private const string TypeManual = "Manual";
    private const string TypeCircular = "Circular";
    private const string TypeCrew = "Crew";
    private const string TypeCategories = "Categories";
    private const string TypeFixedTables = "FixedTables";
    private const string TypeOtherDocuments = "OtherDocuments";
    private const string DeletedKey = "DELETED";

    private readonly DmsContext _dms;
    private readonly CircularsContext _cir;
    private readonly GeneralContext _general;
    private readonly OtherDocumentsContext _other;
    private readonly AttachmentStorage _storage;
    private readonly SqlDb _sql;

    public SyncService(DmsContext dms, CircularsContext cir, GeneralContext general, OtherDocumentsContext other, AttachmentStorage storage,
                       SqlDb sql)
    {
        _storage = storage;
        _sql = sql;
        _dms = dms;
        _cir = cir;
        _general = general;
        _other = other;
    }

    // =========================================================================================
    // Install check and vessel keys
    // =========================================================================================

    private static bool _installed;

    public async Task<bool> IsInstalledAsync()
    {
        if (_installed) return true;
        foreach (var table in new[] { "Sync_VesselItem", "Sync_VesselKey", "Sync_Vessel", "Sync_Log" })
            if (!await _general.TableExistsAsync(table)) return false;
        return _installed = true;
    }

    public async Task<bool> ValidateVesselKeyAsync(int vesselId, string key)
    {
        if (vesselId <= 0 || string.IsNullOrWhiteSpace(key)) return false;

        var storedHash = await _general.SyncVesselKeys.AsNoTracking()
            .Where(k => k.VesselID == vesselId && k.IsActive)
            .Select(k => k.KeyHash)
            .FirstOrDefaultAsync();
        if (storedHash == null) return false;

        var given = Encoding.ASCII.GetBytes(HashKey(key));
        var expected = Encoding.ASCII.GetBytes(storedHash.Trim().ToUpperInvariant());
        if (!CryptographicOperations.FixedTimeEquals(given, expected)) return false;

        var now = DateTime.UtcNow;
        await _general.SyncVesselKeys.Where(k => k.VesselID == vesselId)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.LastUsedUtc, now));
        return true;
    }

    public async Task<string> GenerateVesselKeyAsync(int vesselId, string createdBy)
    {
        var exists = await _general.VVessels.AsNoTracking().AnyAsync(v => v.VesselID == vesselId);
        if (!exists) throw new KeyNotFoundException($"Vessel {vesselId} does not exist.");

        // 32 random bytes, URL-safe text (43 characters). Only the hash is kept.
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var row = await _general.SyncVesselKeys.FirstOrDefaultAsync(k => k.VesselID == vesselId);
        if (row == null) _general.SyncVesselKeys.Add(row = new SyncVesselKey { VesselID = vesselId });
        row.KeyHash = HashKey(key);
        row.IsActive = true;
        row.CreatedUtc = DateTime.UtcNow;
        row.CreatedBy = Truncate(createdBy, 150);
        row.LastUsedUtc = null;
        await _general.SaveChangesAsync();
        _general.Entry(row).State = EntityState.Detached;

        return key;
    }

    public async Task RequestResyncAsync(int vesselId)
    {
        await _general.SyncVesselItems.Where(i => i.VesselID == vesselId).ExecuteDeleteAsync();
    }

    // =========================================================================================
    // Pull
    // =========================================================================================

    public async Task<SyncPullResponse> PullAsync(int vesselId, DateTime since)
    {
        var started = DateTime.UtcNow;
        try
        {
            var response = await BuildPullAsync(vesselId, ToUtc(since));
            var count = response.Manuals.Count + response.Circulars.Count +
                        response.DeletedManualIds.Count + response.DeletedCircularIds.Count;
            await RecordCallAsync(vesselId, "Down", started, count, null);
            return response;
        }
        catch (Exception ex)
        {
            await TryRecordCallAsync(vesselId, "Down", started, ex.Message);
            throw;
        }
    }

    private async Task<SyncPullResponse> BuildPullAsync(int vesselId, DateTime since)
    {
        // Whole milliseconds, so the value the vessel sends back matches PendingUtc (datetime2(3)) exactly.
        var now = DateTime.UtcNow;
        var serverTime = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var response = new SyncPullResponse { ServerTimeUtc = serverTime };

        // 1. No "since": the vessel has nothing yet (first sync, new or rebuilt database on board).
        //    Forget what it was sent before, so everything goes again.
        //    Otherwise everything sent in the pull the vessel has applied is now on board.
        if (since <= DateTime.MinValue.AddDays(1))
        {
            await _general.SyncVesselItems.Where(i => i.VesselID == vesselId).ExecuteDeleteAsync();
        }
        else
        {
            // Normally already done by api/Sync/Confirm; repeated here in case that call was lost.
            await ConfirmAppliedAsync(vesselId, since);
        }

        // Change table (Sync_Outbox, filled by triggers on insert, edit and delete at HQ):
        // rows without the DownloadedUtc marker are waiting for this vessel.
        var outboxInstalled = await IsOutboxInstalledAsync();
        var outbox = new HashSet<(string Type, long Id)>();
        if (outboxInstalled)
            outbox = await LoadOutboxAsync(vesselId);

        var tracked = await LoadTrackedAsync(vesselId);
        // Until the vessel has confirmed at least one item (first sync, or after "Force resync"), send the full
        // list of what it should hold - again if the previous response was lost on the way.
        var firstSync = !tracked.Values.Any(t => (t.Type == TypeManual || t.Type == TypeCircular) && t.Confirmed != null);

        // 2. What the vessel should have, with the current version key of each item.
        var catalog = await LoadCatalogAsync(new[] { vesselId });
        var expected = catalog.ExpectedFor(vesselId);

        string? Confirmed((string, long) k) => tracked.TryGetValue(k, out var t) ? t.Confirmed : null;

        // 3. New or changed items, alerts and high-priority circulars first, manuals from the top of the tree down.
        // An item is sent when the change table has a row waiting for it, or (safety net) when its
        // version key differs from the version the vessel confirmed.
        var changed = expected
            .Where(e => outbox.Contains((e.Type, e.Id)) || Confirmed((e.Type, e.Id)) != e.Key)
            .OrderBy(e => e.Order)
            .ToList();

        var toSend = new List<ExpectedItem>();
        long textBudget = 0;
        foreach (var item in changed)
        {
            if (toSend.Count >= MaxItemsPerPull) break;
            if (toSend.Count > 0 && textBudget + item.TextLength > MaxTextCharsPerPull) break;
            toSend.Add(item);
            textBudget += item.TextLength;
        }
        response.HasMore = toSend.Count < changed.Count;

        // 4. Items the vessel has (or was sent) that it should no longer have.
        var expectedKeys = expected.Select(e => (e.Type, e.Id)).ToHashSet();
        var removed = tracked.Keys
            .Where(k => (k.Type == TypeManual || k.Type == TypeCircular) && !expectedKeys.Contains(k))
            .Union(outbox.Where(k => (k.Type == TypeManual || k.Type == TypeCircular) && !expectedKeys.Contains(k)))
            .ToList();
        response.DeletedManualIds = removed.Where(k => k.Type == TypeManual).Select(k => k.Id).ToList();
        response.DeletedCircularIds = removed.Where(k => k.Type == TypeCircular).Select(k => k.Id).ToList();

        // 5. Full details for what is being sent.
        var manualIds = toSend.Where(i => i.Type == TypeManual).Select(i => i.Id).ToList();
        var circularIds = toSend.Where(i => i.Type == TypeCircular).Select(i => i.Id).ToList();
        response.Manuals = await LoadManualsAsync(catalog, manualIds, vesselId);
        response.Circulars = await LoadCircularsAsync(circularIds, vesselId);

        foreach (var id in manualIds)
            foreach (var rank in catalog.ManualRanks(vesselId, id))
                response.ManualAckRequirements.Add(new SyncManualAckRequirementDto { DM_ManualID = id, UserRank = rank });
        foreach (var id in circularIds)
            foreach (var rank in catalog.CircularRanks(vesselId, id))
                response.CircularAckRequirements.Add(new SyncCircularAckRequirementDto { CIR_MasterID = id, UserRank = rank });

        // 6. Crew and categories go as whole lists, only when they changed.
        var pendingRows = new List<(string Type, long Id, string Key)>();

        var crew = await LoadCrewAsync(vesselId);
        var crewKey = Hash(string.Join("\n", crew.OrderBy(c => c.UserId, StringComparer.Ordinal)
            .Select(c => $"{c.VslStaffId}|{c.UserId}|{c.UserName}|{c.UserRank}|{c.UserDept}|{c.IsDisabled}|{c.Password}")));
        if (outbox.Contains((TypeCrew, 0)) || Confirmed((TypeCrew, 0)) != crewKey)
        {
            response.Crew = crew;
            pendingRows.Add((TypeCrew, 0, crewKey));
        }

        var (manualCategories, circularCategories) = await LoadCategoriesAsync();
        var categoriesKey = Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            m = manualCategories.OrderBy(c => c.DM_CategoryID),
            c = circularCategories.OrderBy(c => c.CIR_CategoryID),
        }));
        if (outbox.Contains((TypeCategories, 0)) || Confirmed((TypeCategories, 0)) != categoriesKey)
        {
            response.ManualCategories = manualCategories;
            response.CircularCategories = circularCategories;
            pendingRows.Add((TypeCategories, 0, categoriesKey));
        }

        // Fixed tables (DM_Status, DM_Settings, CIR_Settings, V_Vessel): copied whole, every row, only when changed.
        // They are small, so the version key is worked out on every pull; no trigger is needed.
        var fixedTables = await LoadFixedTablesAsync();
        var fixedKey = Hash(System.Text.Json.JsonSerializer.Serialize(fixedTables));
        if (outbox.Contains((TypeFixedTables, 0)) || Confirmed((TypeFixedTables, 0)) != fixedKey)
        {
            response.DmStatuses = fixedTables.DmStatuses;
            response.DmSettings = fixedTables.DmSettings;
            response.CirSettings = fixedTables.CirSettings;
            response.Vessels = fixedTables.Vessels;
            pendingRows.Add((TypeFixedTables, 0, fixedKey));
        }

        // Other documents for this vessel: sent whole when anything in them changed.
        var otherDocuments = await LoadOtherDocumentsAsync(vesselId);
        var otherKey = Hash(System.Text.Json.JsonSerializer.Serialize(otherDocuments));
        if (outbox.Contains((TypeOtherDocuments, 0)) || Confirmed((TypeOtherDocuments, 0)) != otherKey)
        {
            response.OtherDocuments = otherDocuments;
            pendingRows.Add((TypeOtherDocuments, 0, otherKey));
        }

        // 7. First sync (or a forced resync): tell the vessel everything it should hold, so it drops anything else.
        if (firstSync)
        {
            response.AllManualIds = expected.Where(e => e.Type == TypeManual).Select(e => e.Id).ToList();
            response.AllCircularIds = expected.Where(e => e.Type == TypeCircular).Select(e => e.Id).ToList();
        }

        // 8. Mark what was sent as pending until the vessel confirms it.
        // Items whose details could not be loaded (deleted in the meantime) are left for the next pull.
        var sentManuals = response.Manuals.Select(m => m.DM_ManualID).ToHashSet();
        var sentCirculars = response.Circulars.Select(c => c.CIR_MasterID).ToHashSet();
        pendingRows.AddRange(toSend
            .Where(i => i.Type == TypeManual ? sentManuals.Contains(i.Id) : sentCirculars.Contains(i.Id))
            .Select(i => (i.Type, i.Id, i.Key)));
        pendingRows.AddRange(removed.Select(k => (k.Type, k.Id, DeletedKey)));
        await MarkPendingAsync(vesselId, pendingRows, serverTime);

        // Change-table rows for what this response carries are now sent; the vessel's next pull confirms them.
        if (outboxInstalled && outbox.Count > 0)
        {
            var sent = pendingRows.Select(r => (r.Type, r.Id)).Where(outbox.Contains).ToList();
            response.SyncRows = await MarkOutboxSentAsync(vesselId, sent, serverTime);
        }

        return response;
    }

    // =========================================================================================
    // Change table (dbo.Sync_Outbox, Database/006_VesselSyncOutbox.sql)
    // =========================================================================================

    private bool? _outboxInstalled;

    private async Task<bool> IsOutboxInstalledAsync()
    {
        _outboxInstalled ??= await _general.TableExistsAsync("Sync_Outbox");
        return _outboxInstalled.Value;
    }

    /// <summary>Items with a change-table row not yet downloaded by the vessel (or by every vessel when null).</summary>
    private async Task<List<(int VesselId, string Type, long Id)>> LoadOutboxRowsAsync(int? vesselId)
    {
        var query = _general.SyncOutbox.AsNoTracking().Where(o => o.DownloadedUtc == null);
        if (vesselId != null) query = query.Where(o => o.VesselID == vesselId.Value);
        var rows = await query.Select(o => new { o.VesselID, o.ItemType, o.ItemID }).Distinct().ToListAsync();
        return rows.Select(r => (r.VesselID, r.ItemType.Trim(), r.ItemID)).ToList();
    }

    private async Task<HashSet<(string Type, long Id)>> LoadOutboxAsync(int vesselId) =>
        (await LoadOutboxRowsAsync(vesselId)).Select(r => (r.Type, r.Id)).ToHashSet();

    /// <summary>Sets SentUtc on the waiting rows of the items being sent and returns those rows for the response.</summary>
    private async Task<List<SyncOutboxRowDto>> MarkOutboxSentAsync(int vesselId, List<(string Type, long Id)> items, DateTime serverTime)
    {
        var rows = new List<SyncOutboxRowDto>();
        foreach (var chunk in items.Chunk(500))
        {
            var wanted = chunk.ToHashSet();
            var itemIds = chunk.Select(c => c.Id).Distinct().ToList();
            var waiting = (await _general.SyncOutbox.AsNoTracking()
                    .Where(o => o.VesselID == vesselId && o.DownloadedUtc == null && itemIds.Contains(o.ItemID))
                    .Select(o => new { o.SyncOutboxID, o.ItemType, o.ItemID, o.Action, o.CreatedUtc })
                    .ToListAsync())
                .Where(o => wanted.Contains((o.ItemType.Trim(), o.ItemID)))
                .ToList();
            if (waiting.Count == 0) continue;

            var ids = waiting.Select(o => o.SyncOutboxID).ToList();
            await _general.SyncOutbox.Where(o => ids.Contains(o.SyncOutboxID))
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.SentUtc, serverTime));
            rows.AddRange(waiting.Select(o => new SyncOutboxRowDto
            {
                SyncOutboxID = o.SyncOutboxID,
                ItemType = o.ItemType.Trim(),
                ItemID = o.ItemID,
                Action = o.Action.Trim(),
                ChangedUtc = DateTime.SpecifyKind(o.CreatedUtc, DateTimeKind.Utc),
            }));
        }

        // Keep the table small: downloaded rows older than 30 days are no longer needed.
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var old = await _general.SyncOutbox.AsNoTracking()
            .Where(o => o.DownloadedUtc < cutoff)
            .OrderBy(o => o.SyncOutboxID)
            .Select(o => o.SyncOutboxID)
            .Take(5000)
            .ToListAsync();
        if (old.Count > 0)
            await _general.SyncOutbox.Where(o => old.Contains(o.SyncOutboxID)).ExecuteDeleteAsync();
        return rows.OrderBy(x => x.SyncOutboxID).ToList();
    }

    /// <summary>
    /// Everything sent up to and including the pull answered at <paramref name="appliedServerTime"/> is on board:
    /// pending version keys become confirmed (removals are forgotten), and change-table rows sent by then get
    /// their DownloadedUtc marker.
    /// </summary>
    private async Task ConfirmAppliedAsync(int vesselId, DateTime appliedServerTime)
    {
        await _general.SyncVesselItems
            .Where(i => i.VesselID == vesselId && i.PendingKey == DeletedKey && i.PendingUtc <= appliedServerTime)
            .ExecuteDeleteAsync();
        await _general.SyncVesselItems
            .Where(i => i.VesselID == vesselId && i.PendingKey != null && i.PendingUtc <= appliedServerTime)
            .ExecuteUpdateAsync(u => u
                .SetProperty(i => i.ConfirmedKey, i => i.PendingKey)
                .SetProperty(i => i.ConfirmedUtc, i => i.PendingUtc)
                .SetProperty(i => i.PendingKey, (string?)null)
                .SetProperty(i => i.PendingUtc, (DateTime?)null));

        if (await IsOutboxInstalledAsync())
        {
            var now = DateTime.UtcNow;
            await _general.SyncOutbox
                .Where(o => o.VesselID == vesselId && o.DownloadedUtc == null && o.SentUtc != null && o.SentUtc <= appliedServerTime)
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.DownloadedUtc, now));
        }
    }

    // =========================================================================================
    // Confirm (the vessel has applied a pull)
    // =========================================================================================

    public async Task<SyncConfirmResult> ConfirmAsync(SyncConfirmRequest request)
    {
        var result = new SyncConfirmResult();
        if (!await IsInstalledAsync()) return result;
        var vesselId = request.VesselID;

        // 1. The rows the vessel names: set the marker (only its own rows, only rows that were sent).
        if (await IsOutboxInstalledAsync())
        {
            foreach (var chunk in request.SyncOutboxIds.Where(id => id > 0).Distinct().Chunk(500))
            {
                var ids = chunk.ToList();
                var now = DateTime.UtcNow;
                result.Confirmed += await _general.SyncOutbox
                    .Where(o => o.VesselID == vesselId && o.DownloadedUtc == null && o.SentUtc != null && ids.Contains(o.SyncOutboxID))
                    .ExecuteUpdateAsync(u => u.SetProperty(o => o.DownloadedUtc, now));
            }
        }

        // 2. Everything that pull sent is on board (version keys, and any row the vessel did not list).
        var applied = ToUtc(request.ServerTimeUtc);
        if (applied > DateTime.MinValue.AddDays(1) && applied <= DateTime.UtcNow.AddMinutes(1))
            await ConfirmAppliedAsync(vesselId, applied);

        return result;
    }

    // =========================================================================================
    // Row-by-row sync (Database/010_Sync_ChangeLog.sql)
    // =========================================================================================

    private sealed record SyncTableInfo(string TableName, int SortOrder, string? BlobColumn, bool AllVessels, string? KeyColumn,
        bool HasVesselColumn, List<string> Columns);

    public async Task<SyncChangesResponse?> GetChangesAsync(int vesselId, int max)
    {
        var started = DateTime.UtcNow;
        try
        {
            if (!await _general.TableExistsAsync("Sync_ChangeLog")) return null;
            max = Math.Clamp(max, 1, 2000);
            var tables = await LoadSyncTablesAsync();

            // The waiting rows, oldest first; one more than asked tells whether more are waiting.
            var waiting = (await _general.SyncChangeLogs.AsNoTracking()
                    .Where(c => c.VesselID == vesselId && c.DownloadedUtc == null)
                    .OrderBy(c => c.SyncChangeID)
                    .Take(max + 1)
                    .Select(c => new { c.SyncChangeID, c.TableName, c.KeyValue })
                    .ToListAsync())
                .Select(c => (Id: c.SyncChangeID, Table: c.TableName, Key: c.KeyValue))
                .ToList();
            var hasMore = waiting.Count > max;
            if (hasMore) waiting.RemoveAt(waiting.Count - 1);

            // One change per record, whatever happened to it in between: the ship gets it as it is now.
            var changes = new List<SyncRowChange>();
            foreach (var group in waiting.GroupBy(w => w.Table, StringComparer.OrdinalIgnoreCase))
            {
                var perKey = group.GroupBy(w => w.Key).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
                if (!tables.TryGetValue(group.Key, out var info) || info.KeyColumn == null)
                {
                    // No longer synced (or no such table): sent as a removal the ship ignores, so it is confirmed.
                    changes.AddRange(perKey.Select(k => new SyncRowChange
                        { ChangeIds = k.Value, TableName = group.Key, Key = k.Key, Action = "D" }));
                    continue;
                }

                var rows = await ReadRecordsAsync(info, perKey.Keys.ToList());
                foreach (var (key, ids) in perKey)
                {
                    var change = new SyncRowChange
                    {
                        ChangeIds = ids,
                        TableName = info.TableName,
                        KeyColumn = info.KeyColumn,
                        Key = key,
                        BlobColumn = info.BlobColumn,
                    };
                    if (rows.TryGetValue(key, out var row) && BelongsToVessel(info, row, vesselId))
                    {
                        // An office-side flag: the ship never needs it (and older ship databases have no such column).
                        row.Remove(OfficeOnlyColumn);
                        change.Row = row;
                    }
                    else
                        change.Action = "D"; // deleted at the office, now for another vessel, or office only
                    changes.Add(change);
                }
            }

            changes = changes
                .OrderBy(c => tables.TryGetValue(c.TableName, out var t) ? t.SortOrder : int.MaxValue)
                .ThenBy(c => c.Key)
                .ToList();

            var now = DateTime.UtcNow;
            await MarkChangesAsync(vesselId, waiting.Select(w => w.Id).ToList(), "SentUtc");
            await RecordCallAsync(vesselId, "Down", started, changes.Count, null);
            return new SyncChangesResponse { ServerTimeUtc = now, HasMore = hasMore, Changes = changes };
        }
        catch (Exception ex)
        {
            await TryRecordCallAsync(vesselId, "Down", started, ex.Message);
            throw;
        }
    }

    public async Task<SyncChangesConfirmResult> ConfirmChangesAsync(int vesselId, IReadOnlyCollection<long> changeIds)
    {
        var confirmed = await MarkChangesAsync(vesselId, changeIds.Distinct().ToList(), "DownloadedUtc");
        return new SyncChangesConfirmResult { Confirmed = confirmed };
    }

    /// <summary>Sets SentUtc or DownloadedUtc (now) on this vessel's change rows, in chunks.</summary>
    private async Task<int> MarkChangesAsync(int vesselId, List<long> ids, string column)
    {
        if (column != "SentUtc" && column != "DownloadedUtc") throw new ArgumentException("Unknown column.", nameof(column));
        var total = 0;
        foreach (var chunk in ids.Chunk(1000))
        {
            var list = chunk.ToList();
            var now = DateTime.UtcNow;
            var rows = _general.SyncChangeLogs.Where(c => c.VesselID == vesselId && list.Contains(c.SyncChangeID));
            total += column == "SentUtc"
                ? await rows.ExecuteUpdateAsync(u => u.SetProperty(c => c.SentUtc, now))
                : await rows.Where(c => c.DownloadedUtc == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.DownloadedUtc, now));
        }
        return total;
    }

    /// <summary>The synced tables (Sync_Table), each with its key, whether it has VesselID, and its columns (dbo.Api_SyncTableColumns).</summary>
    private async Task<Dictionary<string, SyncTableInfo>> LoadSyncTablesAsync()
    {
        var result = new Dictionary<string, SyncTableInfo>(StringComparer.OrdinalIgnoreCase);
        var rows = await _general.Database.SqlQuery<SyncTableColumnRow>($"EXEC dbo.Api_SyncTableColumns").ToListAsync();
        foreach (var r in rows)
        {
            if (!result.TryGetValue(r.TableName, out var info))
            {
                info = new SyncTableInfo(r.TableName, r.SortOrder, r.BlobColumn, r.AllVessels, r.KeyColumn, r.HasVesselColumn, new List<string>());
                result[r.TableName] = info;
            }
            info.Columns.Add(r.ColumnName);
        }
        return result;
    }

    /// <summary>The records with these keys, all columns except the file contents, exactly as stored (dbo.Sync_ReadRecords).</summary>
    private async Task<Dictionary<long, Dictionary<string, object?>>> ReadRecordsAsync(SyncTableInfo info, List<long> keys)
    {
        var result = new Dictionary<long, Dictionary<string, object?>>();
        foreach (var chunk in keys.Chunk(1000))
        {
            var list = new DataTable();
            list.Columns.Add("KeyValue", typeof(long));
            list.Columns.Add("VesselID", typeof(int));
            foreach (var key in chunk.Distinct()) list.Rows.Add(key, DBNull.Value);
            var keysParameter = new Microsoft.Data.SqlClient.SqlParameter("@Keys", SqlDbType.Structured)
            {
                TypeName = "dbo.Sync_RowList",
                Value = list,
            };
            foreach (var row in await _sql.ProcRowsAsync("dbo.Sync_ReadRecords", ("@TableName", info.TableName), ("@Keys", keysParameter)))
                result[Convert.ToInt64(row[info.KeyColumn!])] = row;
        }
        return result;
    }

    /// <summary>Records with this column set to 1 stay at the office (Other Documents added there, Database/017).</summary>
    private const string OfficeOnlyColumn = "IsForOfficeOnly";

    /// <summary>
    /// A record goes to a vessel when it is for that vessel, or for all of them (VesselID -1, or no vessel column),
    /// and it is not office only (IsForOfficeOnly = 1).
    /// </summary>
    private static bool BelongsToVessel(SyncTableInfo info, Dictionary<string, object?> row, int vesselId)
    {
        if (row.TryGetValue(OfficeOnlyColumn, out var officeOnly) && officeOnly != null && Convert.ToInt64(officeOnly) == 1)
            return false;
        if (info.AllVessels || !info.HasVesselColumn) return true;
        if (!row.TryGetValue("VesselID", out var value) || value == null) return true;
        var recordVessel = Convert.ToInt64(value);
        // -1 (and the old 0) = every vessel; -2 = office only (Database/012_NewFeatures.sql), never sent.
        return recordVessel is -1 or 0 || recordVessel == vesselId;
    }

    // =========================================================================================
    // Push (acknowledgements made on board)
    // =========================================================================================

    public async Task<SyncPushResult> PushAsync(SyncPushRequest request)
    {
        var started = DateTime.UtcNow;
        try
        {
            var result = await ApplyPushAsync(request);
            await RecordCallAsync(request.VesselID, "Up", started, result.ManualAcksAccepted + result.CircularAcksAccepted, null);
            return result;
        }
        catch (Exception ex)
        {
            await TryRecordCallAsync(request.VesselID, "Up", started, ex.Message);
            throw;
        }
    }

    private async Task<SyncPushResult> ApplyPushAsync(SyncPushRequest request)
    {
        var result = new SyncPushResult();
        var vesselId = request.VesselID;

        // ---- Manuals ----
        if (request.ManualAcks.Count > 0)
        {
            var versionIds = request.ManualAcks.Select(a => a.DM_ManualVersionID).Distinct().ToList();
            var versions = await _dms.DmManualVersions.AsNoTracking()
                .Where(v => versionIds.Contains(v.DM_ManualVersionID))
                .Select(v => new { v.DM_ManualVersionID, v.DM_ManualID, v.Version })
                .ToDictionaryAsync(v => v.DM_ManualVersionID);
            var existing = (await _dms.DmManualUserAcks.AsNoTracking()
                    .Where(a => a.VesselID == vesselId && versionIds.Contains(a.DM_ManualVersionID))
                    .Select(a => new { a.DM_ManualVersionID, a.UserId })
                    .ToListAsync())
                .Select(a => (a.DM_ManualVersionID, a.UserId.Trim().ToUpperInvariant()))
                .ToHashSet();

            foreach (var ack in request.ManualAcks)
            {
                if (string.IsNullOrWhiteSpace(ack.UserId)) continue;
                if (!versions.TryGetValue(ack.DM_ManualVersionID, out var version))
                {
                    result.Warnings.Add($"Manual acknowledgement for unknown version {ack.DM_ManualVersionID} was skipped.");
                    result.SkippedManualAcks.Add($"{ack.DM_ManualVersionID}:{ack.UserId}");
                    continue;
                }
                if (!existing.Add((ack.DM_ManualVersionID, ack.UserId.Trim().ToUpperInvariant())))
                {
                    result.ManualAcksAlreadyApplied++;
                    continue;
                }

                // The ID given on board (Proc_NextID, this vessel's range) when free here, else a new one in that range.
                _dms.DmManualUserAcks.Add(new DmManualUserAck
                {
                    DmManualUserAckId = await _dms.KeepOrNextIdAsync<DmManualUserAck>(ack.DM_Manual_UserAckID, TableIdExtensions.OriginVessel, vesselId),
                    Origin = "VSL",
                    VesselID = vesselId,
                    DM_ManualID = version.DM_ManualID,
                    DM_ManualVersionID = ack.DM_ManualVersionID,
                    Version = LeadingNumber(version.Version) ?? ack.Version,
                    UserId = ack.UserId.Trim(),
                    UserRank = ack.UserRank,
                    UserName = ack.UserName,
                    DateRead = ToUtc(ack.DateRead),
                    Comments = Truncate(ack.Comments, 500),
                    IsActive = 1,
                });
                result.ManualAcksAccepted++;
            }
            await _dms.SaveChangesAsync();
        }

        // ---- Circulars ----
        if (request.CircularAcks.Count > 0)
        {
            var crew = (await _general.VslStaffs.AsNoTracking()
                    .Where(s => s.VesselID == vesselId)
                    .Select(s => new { s.UserId, s.VslStaffId })
                    .ToListAsync())
                .GroupBy(s => s.UserId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().VslStaffId, StringComparer.OrdinalIgnoreCase);

            var circularIds = request.CircularAcks.Select(a => a.CIR_MasterID).Distinct().ToList();
            var knownCirculars = (await _cir.CIR_Master.AsNoTracking()
                .Where(c => circularIds.Contains(c.CIR_MasterID))
                .Select(c => c.CIR_MasterID)
                .ToListAsync()).ToHashSet();
            var existing = (await _cir.CIR_UserAck.AsNoTracking()
                    .Where(a => a.VesselID == vesselId && a.CIR_MasterID != null && circularIds.Contains(a.CIR_MasterID.Value))
                    .Select(a => new { a.CIR_MasterID, a.Fleet_CrewID })
                    .ToListAsync())
                .Select(a => (a.CIR_MasterID!.Value, a.Fleet_CrewID ?? 0))
                .ToHashSet();

            foreach (var ack in request.CircularAcks)
            {
                if (!crew.TryGetValue((ack.UserId ?? "").Trim(), out var crewId))
                {
                    result.Warnings.Add($"Circular acknowledgement by unknown crew member '{ack.UserId}' was skipped.");
                    result.SkippedCircularAcks.Add($"{ack.CIR_MasterID}:{ack.UserId}");
                    continue;
                }
                if (!knownCirculars.Contains(ack.CIR_MasterID))
                {
                    result.Warnings.Add($"Acknowledgement for unknown circular {ack.CIR_MasterID} was skipped.");
                    result.SkippedCircularAcks.Add($"{ack.CIR_MasterID}:{ack.UserId}");
                    continue;
                }
                if (!existing.Add((ack.CIR_MasterID, crewId)))
                {
                    result.CircularAcksAlreadyApplied++;
                    continue;
                }

                _cir.CIR_UserAck.Add(new CIR_UserAck
                {
                    CIR_UserAckID = await _cir.KeepOrNextIdAsync<CIR_UserAck>(ack.CIR_UserAckID, TableIdExtensions.OriginVessel, vesselId),
                    Origin = "VSL",
                    VesselID = vesselId,
                    CIR_MasterID = ack.CIR_MasterID,
                    Fleet_CrewID = crewId,
                    DateRead = ToUtc(ack.DateRead),
                    Comments = Truncate(ack.Comments, 500),
                    IsActive = 1,
                });
                result.CircularAcksAccepted++;
            }
            await _cir.SaveChangesAsync();
        }

        // The office rows for what was sent (new or already stored), so the vessel can replace its own
        // pending rows (negative IDs) with them.
        if (request.ManualAcks.Count > 0)
        {
            var versionIds = request.ManualAcks.Select(a => a.DM_ManualVersionID).Distinct().ToList();
            var users = request.ManualAcks.Select(a => (a.UserId ?? "").Trim().ToUpperInvariant()).ToHashSet();
            result.ManualAckRows = (await _dms.DmManualUserAcks.AsNoTracking()
                    .Where(a => a.VesselID == vesselId && versionIds.Contains(a.DM_ManualVersionID))
                    .Select(a => new SyncDMManualUserAckRow
                    {
                        DM_Manual_UserAckID = a.DmManualUserAckId,
                        Origin = a.Origin,
                        VesselID = a.VesselID,
                        DM_ManualID = a.DM_ManualID,
                        DM_ManualVersionID = a.DM_ManualVersionID,
                        Version = a.Version,
                        UserRank = a.UserRank,
                        UserName = a.UserName,
                        UserID = a.UserId,
                        DateRead = a.DateRead,
                        Comments = a.Comments,
                        IsActive = a.IsActive,
                    })
                    .ToListAsync())
                .Where(r => users.Contains(r.UserID.Trim().ToUpperInvariant()))
                .ToList();
        }
        if (request.CircularAcks.Count > 0)
        {
            var circularIds = request.CircularAcks.Select(a => a.CIR_MasterID).Distinct().ToList();
            result.CircularAckRows = await _cir.CIR_UserAck.AsNoTracking()
                .Where(a => a.VesselID == vesselId && a.CIR_MasterID != null && circularIds.Contains(a.CIR_MasterID.Value))
                .Select(a => new SyncCIRUserAckRow
                {
                    CIR_UserAckID = a.CIR_UserAckID,
                    Origin = a.Origin,
                    VesselID = a.VesselID,
                    CIR_MasterID = a.CIR_MasterID,
                    Fleet_CrewID = a.Fleet_CrewID,
                    DateRead = a.DateRead,
                    Comments = a.Comments,
                    IsActive = a.IsActive,
                })
                .ToListAsync();
        }

        return result;
    }

    // =========================================================================================
    // Push (other documents added on board)
    // =========================================================================================

    public async Task<OtherDocumentPushResult> PushOtherDocumentAsync(int vesselId, OtherDocumentPushDto doc, IReadOnlyList<byte[]> files)
    {
        var started = DateTime.UtcNow;
        try
        {
            var result = await ApplyOtherDocumentPushAsync(vesselId, doc, files);
            await RecordCallAsync(vesselId, "Up", started, result.Stored && !result.AlreadyApplied ? 1 : 0, result.Error);
            return result;
        }
        catch (Exception ex)
        {
            await TryRecordCallAsync(vesselId, "Up", started, ex.Message);
            throw;
        }
    }

    private async Task<OtherDocumentPushResult> ApplyOtherDocumentPushAsync(int vesselId, OtherDocumentPushDto doc, IReadOnlyList<byte[]> files)
    {
        var number = (doc.DocumentNumber ?? "").Trim();
        var title = (doc.DocumentTitle ?? "").Trim();
        if (number.Length == 0 || title.Length == 0)
            return new OtherDocumentPushResult { Error = "The document has no number or title." };
        if (doc.Files.Count != files.Count)
            return new OtherDocumentPushResult { Error = $"{doc.Files.Count} file(s) described but {files.Count} sent." };
        if (!await _other.OtherDocument_Type.AsNoTracking().AnyAsync(t => t.OtherDocument_TypeID == doc.OtherDocument_TypeID))
            return new OtherDocumentPushResult { Error = $"Document type {doc.OtherDocument_TypeID} is not known at the office." };

        // Sent before (the answer was lost on the way back, or the vessel sends it again because one of its files has
        // not reached the office): keep what is held and add the files that are not here yet.
        var held = await _other.OtherDocumentMaster.AsNoTracking()
            .Where(m => m.VesselID == vesselId && m.Origin == "VSL" && (m.IsDeleted ?? 0) != 1
                        && m.OtherDocument_TypeID == doc.OtherDocument_TypeID && m.DocumentNumber == number)
            .Select(m => (long?)m.OtherDocumentMasterID)
            .FirstOrDefaultAsync();
        if (held != null)
        {
            var heldFiles = await _other.OtherDocument_Blob.AsNoTracking()
                .Where(b => b.OtherDocumentMasterID == held.Value)
                .Select(b => new { b.OtherDocument_BlobID, b.FileName, b.BlobSize })
                .ToListAsync();
            var added = 0;
            for (var i = 0; i < files.Count; i++)
            {
                var f = doc.Files[i];
                if (files[i].Length == 0) continue; // a file the vessel only knows by name (no contents on board)
                var name = Truncate(f.FileName, 150);
                if (heldFiles.Any(h => h.OtherDocument_BlobID == f.VesselFileID || (h.FileName == name && h.BlobSize == files[i].Length)))
                    continue;
                await AddPushedFileAsync(vesselId, held.Value, f, files[i]);
                added++;
            }
            if (added > 0)
            {
                await _other.SaveChangesAsync();
                _other.ChangeTracker.Clear();
            }
            return await OtherDocumentPushResultAsync(held.Value, doc, alreadyApplied: added == 0);
        }

        var uploadedOn = doc.UploadedOn.HasValue ? ToUtc(doc.UploadedOn.Value) : DateTime.UtcNow;
        // The IDs given on board (Proc_NextID, this vessel's range) when free here, else new ones in that range.
        var masterId = await _other.KeepOrNextIdAsync<OtherDocumentMaster>(doc.VesselDocumentID, TableIdExtensions.OriginVessel, vesselId);
        _other.OtherDocumentMaster.Add(new OtherDocumentMaster
        {
            OtherDocumentMasterID = masterId,
            Origin = "VSL",
            VesselID = vesselId,
            OtherDocument_TypeID = doc.OtherDocument_TypeID,
            DocumentNumber = Truncate(number, 50)!,
            DocumentTitle = Truncate(title, 500)!,
            Status = Truncate(doc.Status?.Trim(), 50),
            Reference = Truncate(doc.Reference?.Trim(), 150),
            Comments = Truncate(doc.Comments?.Trim(), 500),
            UploadedOn = uploadedOn,
            UploadedBy = Truncate(doc.UploadedBy?.Trim(), 150),
            IsDeleted = 0,
            ModifiedBy = Truncate(doc.UploadedBy?.Trim(), 50),
            ModifiedDate = uploadedOn,
            OtherDocument_FolderID = doc.OtherDocument_FolderID,
        });

        for (var i = 0; i < files.Count; i++)
            await AddPushedFileAsync(vesselId, masterId, doc.Files[i], files[i]);

        // No log row here: the vessel wrote its own "Added" log when the document was added on board.
        await _other.SaveChangesAsync(); // one transaction: the document and its files, or nothing
        _other.ChangeTracker.Clear();     // the file bytes are not needed in memory any longer

        return await OtherDocumentPushResultAsync(masterId, doc, alreadyApplied: false);
    }

    /// <summary>Adds (not yet saved) one file a vessel sent with a document, under the ID given on board when it is free here.</summary>
    private async Task AddPushedFileAsync(int vesselId, long masterId, OtherDocumentPushFileDto f, byte[] bytes)
    {
        var fileId = await _other.KeepOrNextIdAsync<OtherDocument_Blob>(f.VesselFileID, TableIdExtensions.OriginVessel, vesselId);
        var fileName = Truncate(f.FileName, 150);
        // Kept at the office in the database, a local folder or the cloud, as BlobTableSettings says for OtherDocument_Blob.
        var stored = await _storage.SaveAsync(AttachmentStorage.OtherDocumentTable, fileId, fileName, bytes);
        _other.OtherDocument_Blob.Add(new OtherDocument_Blob
        {
            OtherDocument_BlobID = fileId,
            OtherDocumentMasterID = masterId,
            Origin = "VSL",
            VesselID = vesselId,
            Description = Truncate(f.Description ?? f.FileName, 150),
            FileName = fileName,
            FileType = Truncate(f.FileType, 50),
            OriginalFileType = Truncate(f.OriginalFileType, 50),
            BlobSize = bytes.Length,
            BlobContents = stored.BlobContents,
            AddedOn = f.AddedOn.HasValue ? ToUtc(f.AddedOn.Value) : DateTime.UtcNow,
            Cloud_ContainerName = stored.Cloud_ContainerName,
            Cloud_Directory = stored.Cloud_Directory,
            HQ_Storage_Option = stored.HQ_Storage_Option,
            HQ_Local_Folder = stored.HQ_Local_Folder,
            HQ_DatabaseName = stored.HQ_DatabaseName,
            VSL_Storage_Option = stored.VSL_Storage_Option,
            VSL_Local_Folder = stored.VSL_Local_Folder,
            VSL_DatabaseName = stored.VSL_DatabaseName,
        });
    }

    /// <summary>The office rows for a document a vessel sent, read back as stored (datetime precision included).</summary>
    private async Task<OtherDocumentPushResult> OtherDocumentPushResultAsync(long masterId, OtherDocumentPushDto doc, bool alreadyApplied)
    {
        var m = await _other.OtherDocumentMaster.AsNoTracking().FirstAsync(x => x.OtherDocumentMasterID == masterId);
        var blobs = await _other.OtherDocument_Blob.AsNoTracking()
            .Where(b => b.OtherDocumentMasterID == masterId)
            .OrderBy(b => b.OtherDocument_BlobID)
            .Select(b => new SyncOtherDocumentBlobRow
            {
                OtherDocument_BlobID = b.OtherDocument_BlobID,
                OtherDocumentMasterID = b.OtherDocumentMasterID,
                Origin = b.Origin,
                VesselID = b.VesselID,
                Description = b.Description,
                FileName = b.FileName,
                FileType = b.FileType,
                OriginalFileType = b.OriginalFileType,
                BlobSize = b.BlobSize,
                AddedOn = b.AddedOn,
                Cloud_ContainerName = b.Cloud_ContainerName,
                Cloud_Directory = b.Cloud_Directory,
                HQ_Storage_Option = b.HQ_Storage_Option,
                HQ_DatabaseName = b.HQ_DatabaseName,
                HQ_Local_Folder = b.HQ_Local_Folder,
                VSL_Storage_Option = b.VSL_Storage_Option,
                VSL_DatabaseName = b.VSL_DatabaseName,
                VSL_Local_Folder = b.VSL_Local_Folder,
            })
            .ToListAsync();

        // Stored in the order sent, so file i is doc.Files[i]. On a repeat, also match by name and size.
        var vesselIds = new List<long>();
        var unmatched = doc.Files.ToList();
        foreach (var b in blobs)
        {
            var f = unmatched.FirstOrDefault(x => x.FileName == b.FileName && x.BlobSize == b.BlobSize) ?? unmatched.FirstOrDefault();
            if (f != null) unmatched.Remove(f);
            vesselIds.Add(f?.VesselFileID ?? 0);
        }

        return new OtherDocumentPushResult
        {
            Stored = true,
            AlreadyApplied = alreadyApplied,
            Document = new SyncOtherDocumentMasterRow
            {
                OtherDocumentMasterID = m.OtherDocumentMasterID,
                Origin = m.Origin,
                VesselID = m.VesselID,
                OtherDocument_TypeID = m.OtherDocument_TypeID,
                DocumentNumber = m.DocumentNumber,
                DocumentTitle = m.DocumentTitle,
                Status = m.Status,
                Reference = m.Reference,
                Comments = m.Comments,
                UploadedOn = m.UploadedOn,
                UploadedBy = m.UploadedBy,
                IsDeleted = m.IsDeleted,
                ModifiedBy = m.ModifiedBy,
                ModifiedDate = m.ModifiedDate,
            },
            Files = blobs,
            VesselFileIDs = vesselIds,
        };
    }

    // =========================================================================================
    // Heartbeat and attachments
    // =========================================================================================

    public async Task HeartbeatAsync(SyncHeartbeatRequest request)
    {
        var pending = Math.Max(0, request.PendingManualAcks) + Math.Max(0, request.PendingCircularAcks);
        int? progress = request.ProgressPercent is int p ? Math.Clamp(p, 0, 100) : null;
        var failed = !request.LastCycleSucceeded;

        var row = await SyncVesselRowAsync(request.VesselID);
        row.LastContactUtc = DateTime.UtcNow;
        row.PendingOnShip = pending;
        row.CurrentActivity = Truncate(request.Activity, 100);
        row.ProgressPercent = progress;
        row.AppVersion = Truncate(request.AppVersion, 50) ?? row.AppVersion;
        if (failed)
        {
            row.LastStatus = "Failed";
            row.LastError = Truncate(request.LastError, 1000);
        }
        await SaveSyncVesselAsync(row);
    }

    public async Task<(byte[] Bytes, string FileType, string? FileName)?> GetManualAttachmentBytesAsync(int vesselId, long attachmentId)
    {
        var a = await _dms.DmManualAttachments.AsNoTracking()
            .Where(x => x.DmManualAttachmentsId == attachmentId && (x.IsDeleted ?? 0) != 1)
            .Select(x => new { x.DM_ManualID, x.BlobContents, x.FileType })
            .FirstOrDefaultAsync();
        if (a?.BlobContents == null) return null;
        if (!await IsTrackedAsync(vesselId, TypeManual, a.DM_ManualID)) return null; // not sent to this vessel
        return (a.BlobContents, a.FileType, null);
    }

    public async Task<(byte[] Bytes, string FileType, string? FileName)?> GetCircularAttachmentBytesAsync(int vesselId, long attachmentId)
    {
        var b = await _cir.CIR_Blob.AsNoTracking()
            .Where(x => x.CIR_BlobID == attachmentId)
            .Select(x => new { x.CIR_MasterID, x.BlobContents, x.FileType, x.FileName,
                               x.HQ_Storage_Option, x.HQ_Local_Folder, x.Cloud_ContainerName, x.Cloud_Directory })
            .FirstOrDefaultAsync();
        if (b == null || b.CIR_MasterID == null) return null;
        if (!await IsTrackedAsync(vesselId, TypeCircular, b.CIR_MasterID.Value)) return null;
        // In the database, a local folder or the cloud (BlobTableSettings).
        var bytes = await _storage.ReadAsync(AttachmentStorage.CircularTable, new AttachmentRef(attachmentId, b.FileName, b.BlobContents,
            b.HQ_Storage_Option, b.HQ_Local_Folder, b.Cloud_ContainerName, b.Cloud_Directory));
        if (bytes == null) return null;
        return (bytes, b.FileType ?? "application/octet-stream", b.FileName);
    }

    public async Task<(byte[] Bytes, string FileType, string? FileName)?> GetOtherDocumentFileBytesAsync(int vesselId, long blobId)
    {
        var f = await (
                from b in _other.OtherDocument_Blob.AsNoTracking()
                join m in _other.OtherDocumentMaster.AsNoTracking() on b.OtherDocumentMasterID equals m.OtherDocumentMasterID
                where b.OtherDocument_BlobID == blobId && (m.VesselID == vesselId || m.VesselID == -1) && (m.IsDeleted ?? 0) != 1
                      && m.IsForOfficeOnly != 1
                select new { b.BlobContents, b.FileType, b.OriginalFileType, b.FileName,
                             b.HQ_Storage_Option, b.HQ_Local_Folder, b.Cloud_ContainerName, b.Cloud_Directory })
            .FirstOrDefaultAsync();
        if (f == null) return null;
        // In the database, a local folder or the cloud (BlobTableSettings).
        var bytes = await _storage.ReadAsync(AttachmentStorage.OtherDocumentTable, new AttachmentRef(blobId, f.FileName, f.BlobContents,
            f.HQ_Storage_Option, f.HQ_Local_Folder, f.Cloud_ContainerName, f.Cloud_Directory));
        if (bytes == null) return null;
        return (bytes, f.FileType ?? f.OriginalFileType ?? "application/octet-stream", f.FileName);
    }

    /// <summary>Every document type, and this vessel's documents (or all vessels' -1) with their file details.</summary>
    private async Task<SyncOtherDocumentsDto> LoadOtherDocumentsAsync(int vesselId)
    {
        var types = await _other.OtherDocument_Type.AsNoTracking()
            .OrderBy(t => t.OtherDocument_TypeID)
            .Select(t => new SyncOtherDocumentTypeRow
            {
                OtherDocument_TypeID = t.OtherDocument_TypeID,
                Origin = t.Origin,
                VesselID = t.VesselID,
                DocumentCode = t.DocumentCode,
                DocumentType = t.DocumentType,
                IsActive = t.IsActive,
                SortOrder = t.SortOrder,
            })
            .ToListAsync();
        var documents = await _other.OtherDocumentMaster.AsNoTracking()
            .Where(m => (m.VesselID == vesselId || m.VesselID == -1) && (m.IsDeleted ?? 0) != 1 && m.IsForOfficeOnly != 1)
            .OrderBy(m => m.OtherDocumentMasterID)
            .Select(m => new SyncOtherDocumentMasterRow
            {
                OtherDocumentMasterID = m.OtherDocumentMasterID,
                Origin = m.Origin,
                VesselID = m.VesselID,
                OtherDocument_TypeID = m.OtherDocument_TypeID,
                DocumentNumber = m.DocumentNumber,
                DocumentTitle = m.DocumentTitle,
                Status = m.Status,
                Reference = m.Reference,
                Comments = m.Comments,
                UploadedOn = m.UploadedOn,
                UploadedBy = m.UploadedBy,
                IsDeleted = m.IsDeleted,
                ModifiedBy = m.ModifiedBy,
                ModifiedDate = m.ModifiedDate,
            })
            .ToListAsync();
        var ids = documents.Select(d => d.OtherDocumentMasterID).ToList();
        var files = ids.Count == 0 ? new List<SyncOtherDocumentBlobRow>() : await _other.OtherDocument_Blob.AsNoTracking()
            .Where(b => ids.Contains(b.OtherDocumentMasterID))
            .OrderBy(b => b.OtherDocument_BlobID)
            .Select(b => new SyncOtherDocumentBlobRow
            {
                OtherDocument_BlobID = b.OtherDocument_BlobID,
                OtherDocumentMasterID = b.OtherDocumentMasterID,
                Origin = b.Origin,
                VesselID = b.VesselID,
                Description = b.Description,
                FileName = b.FileName,
                FileType = b.FileType,
                OriginalFileType = b.OriginalFileType,
                BlobSize = b.BlobSize,
                AddedOn = b.AddedOn,
                Cloud_ContainerName = b.Cloud_ContainerName,
                Cloud_Directory = b.Cloud_Directory,
                HQ_Storage_Option = b.HQ_Storage_Option,
                HQ_DatabaseName = b.HQ_DatabaseName,
                HQ_Local_Folder = b.HQ_Local_Folder,
                VSL_Storage_Option = b.VSL_Storage_Option,
                VSL_DatabaseName = b.VSL_DatabaseName,
                VSL_Local_Folder = b.VSL_Local_Folder,
            })
            .ToListAsync();
        return new SyncOtherDocumentsDto { Types = types, Documents = documents, Files = files };
    }

    // =========================================================================================
    // Sync monitor
    // =========================================================================================

    public async Task<Dictionary<int, HashSet<(string ItemType, long ItemId)>>> GetQueuedItemsAsync(IReadOnlyCollection<int> vesselIds)
    {
        var result = new Dictionary<int, HashSet<(string ItemType, long ItemId)>>();
        if (vesselIds.Count == 0 || !await IsInstalledAsync()) return result;

        var catalog = await LoadCatalogAsync(vesselIds);
        // One vessel (the vessel details page): read only its rows, not the whole fleet's.
        int? only = vesselIds.Distinct().Count() == 1 ? vesselIds.First() : null;
        var trackedByVessel = (await LoadTrackedRowsAsync(only))
            .GroupBy(t => t.VesselId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(t => (t.Type, t.Id)));
        var outboxByVessel = await IsOutboxInstalledAsync()
            ? (await LoadOutboxRowsAsync(only))
                .Where(r => r.Type == TypeManual || r.Type == TypeCircular)
                .ToLookup(r => r.VesselId, r => (r.Type, r.Id))
            : Enumerable.Empty<(int VesselId, string Type, long Id)>().ToLookup(r => r.VesselId, r => (r.Type, r.Id));

        foreach (var vesselId in vesselIds.Distinct())
        {
            trackedByVessel.TryGetValue(vesselId, out var rows);
            rows ??= new Dictionary<(string, long), TrackedRow>();

            var expected = catalog.ExpectedFor(vesselId);
            var queued = expected
                .Where(e => !rows.TryGetValue((e.Type, e.Id), out var t) || t.Confirmed != e.Key)
                .Select(e => (e.Type, e.Id))
                .ToHashSet();

            // Still on board but no longer meant for this vessel: a removal is waiting to go out.
            var expectedKeys = expected.Select(e => (e.Type, e.Id)).ToHashSet();
            foreach (var k in rows.Keys)
                if ((k.Item1 == TypeManual || k.Item1 == TypeCircular) && !expectedKeys.Contains(k))
                    queued.Add(k);

            // Changes recorded in the change table and not downloaded yet.
            foreach (var k in outboxByVessel[vesselId])
                queued.Add(k);

            result[vesselId] = queued;
        }
        return result;
    }

    // =========================================================================================
    // Catalog: what each vessel should have, with version keys
    // =========================================================================================

    private sealed record ExpectedItem(string Type, long Id, string Key, int Order, long TextLength);

    private sealed record ManualMeta(long Id, long ParentId, int Level, string BaseKey, bool HasVersion, long? VersionId, long TextLength);

    private sealed record CircularMeta(long Id, string BaseKey, int Order);

    private sealed class Catalog
    {
        public Dictionary<long, ManualMeta> Manuals { get; } = new();
        public Dictionary<long, CircularMeta> Circulars { get; } = new();
        /// <summary>(vesselId or -1) -> manual -> ranks</summary>
        public Dictionary<int, Dictionary<long, SortedSet<string>>> ManualAssign { get; } = new();
        public Dictionary<int, Dictionary<long, SortedSet<string>>> CircularAssign { get; } = new();

        private static IEnumerable<string> Ranks(Dictionary<int, Dictionary<long, SortedSet<string>>> map, int vesselId, long id)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in new[] { vesselId, -1 })
                if (map.TryGetValue(v, out var byItem) && byItem.TryGetValue(id, out var ranks))
                    set.UnionWith(ranks);
            return set;
        }

        public IEnumerable<string> ManualRanks(int vesselId, long id) => Ranks(ManualAssign, vesselId, id);
        public IEnumerable<string> CircularRanks(int vesselId, long id) => Ranks(CircularAssign, vesselId, id);

        private static IEnumerable<long> Assigned(Dictionary<int, Dictionary<long, SortedSet<string>>> map, int vesselId) =>
            new[] { vesselId, -1 }.SelectMany(v => map.TryGetValue(v, out var byItem) ? byItem.Keys : Enumerable.Empty<long>()).Distinct();

        public List<ExpectedItem> ExpectedFor(int vesselId)
        {
            var items = new List<ExpectedItem>();

            // Manuals with a released version, and every manual above them so the tree is complete on board.
            var manualIds = new HashSet<long>();
            foreach (var id in Assigned(ManualAssign, vesselId))
            {
                if (!Manuals.TryGetValue(id, out var m) || !m.HasVersion) continue;
                var current = m;
                while (current != null && manualIds.Add(current.Id))
                    current = current.ParentId != 0 && Manuals.TryGetValue(current.ParentId, out var parent) ? parent : null;
            }
            foreach (var id in manualIds)
            {
                var m = Manuals[id];
                var key = Hash($"{m.BaseKey}|r:{string.Join(",", ManualRanks(vesselId, id))}");
                items.Add(new ExpectedItem(TypeManual, id, key, 1_000 + m.Level, m.TextLength));
            }

            foreach (var id in Assigned(CircularAssign, vesselId))
            {
                if (!Circulars.TryGetValue(id, out var c)) continue;
                var key = Hash($"{c.BaseKey}|r:{string.Join(",", CircularRanks(vesselId, id))}");
                items.Add(new ExpectedItem(TypeCircular, id, key, c.Order, 0));
            }
            return items;
        }
    }

    /// <summary>Loads the metadata needed to work out what the given vessels should have (no manual text or files).</summary>
    private async Task<Catalog> LoadCatalogAsync(IReadOnlyCollection<int> vesselIds)
    {
        var catalog = new Catalog();
        var ids = vesselIds.Distinct().ToList();

        // ---- Manuals ----
        var manualAssign = await _dms.DmManualToAckVsls.AsNoTracking()
            .Where(a => (ids.Contains(a.VesselID) || a.VesselID == -1) && (a.IsActive ?? 1) == 1)
            .Select(a => new { a.VesselID, a.DM_ManualID, a.UserRank })
            .ToListAsync();
        foreach (var a in manualAssign)
            AddRank(catalog.ManualAssign, a.VesselID, a.DM_ManualID, a.UserRank);

        // One-time full copy (Database/008_Sync_FullCopy.sql): items given to a vessel whether or not they are
        // assigned to it. They count as assigned with no ranks to acknowledge.
        var fullCopy = await LoadFullCopyAsync(ids);
        foreach (var f in fullCopy.Where(f => f.Type == TypeManual))
            AddRank(catalog.ManualAssign, f.VesselId, f.Id, null);

        // Only the assigned manuals and the manuals above them are needed. Work that set out from a
        // narrow (id, parent) list first, so the heavy reads below (version text length, attachments)
        // touch those manuals only instead of the whole library.
        var parentOf = (await _dms.DmManuals.AsNoTracking()
                .Where(m => (m.IsDeleted ?? 0) != 1)
                .Select(m => new { m.DM_ManualID, m.ParentId })
                .ToListAsync())
            .ToDictionary(m => m.DM_ManualID, m => m.ParentId);
        var neededManuals = new HashSet<long>();
        foreach (var start in manualAssign.Select(a => a.DM_ManualID)
                     .Concat(fullCopy.Where(f => f.Type == TypeManual).Select(f => f.Id)).Distinct())
        {
            var id = start;
            while (parentOf.TryGetValue(id, out var parentId) && neededManuals.Add(id) && parentId != 0)
                id = parentId;
        }
        var neededManualIds = neededManuals.ToList();

        var manuals = await _dms.DmManuals.AsNoTracking()
            .Where(m => neededManualIds.Contains(m.DM_ManualID))
            .Select(m => new
            {
                m.DM_ManualID, m.ParentId, m.ManualLevel, m.DM_CategoryID, m.ManualNo, m.ManualCode, m.ManualName,
                m.IsMainTitle, m.IsAcknowledgementRequired, m.ModifiedDate,
            })
            .ToListAsync();
        var versions = (await _dms.DmManualVersions.AsNoTracking()
                .Where(v => v.IsActive == 1 && neededManualIds.Contains(v.DM_ManualID))
                .Select(v => new
                {
                    v.DM_ManualID, v.DM_ManualVersionID, v.Version, v.PublishedDate, v.ReleasedDate, v.LastModifiedDate,
                    TextLength = (long?)v.TextContents!.Length,
                })
                .ToListAsync())
            .GroupBy(v => v.DM_ManualID)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.DM_ManualVersionID).First());
        var attachments = (await _dms.DmManualAttachments.AsNoTracking()
                .Where(a => (a.IsDeleted ?? 0) != 1 && neededManualIds.Contains(a.DM_ManualID))
                .Select(a => new { a.DmManualAttachmentsId, a.DM_ManualID, a.BlobSize, a.FileType })
                .ToListAsync())
            .ToLookup(a => a.DM_ManualID);

        foreach (var m in manuals)
        {
            versions.TryGetValue(m.DM_ManualID, out var v);
            var files = string.Join(",", attachments[m.DM_ManualID].OrderBy(a => a.DmManualAttachmentsId)
                .Select(a => $"{a.DmManualAttachmentsId}:{a.BlobSize}:{a.FileType}"));
            var baseKey = string.Join("|", m.ParentId, m.ManualLevel, m.DM_CategoryID, m.ManualNo, m.ManualCode, m.ManualName,
                m.IsMainTitle, m.IsAcknowledgementRequired, Ticks(m.ModifiedDate),
                v?.DM_ManualVersionID, v?.Version, Ticks(v?.PublishedDate), Ticks(v?.ReleasedDate), Ticks(v?.LastModifiedDate),
                v?.TextLength, files);
            catalog.Manuals[m.DM_ManualID] = new ManualMeta(m.DM_ManualID, m.ParentId, m.ManualLevel, baseKey,
                v != null, v?.DM_ManualVersionID, v?.TextLength ?? 0);
        }

        // ---- Circulars ----
        var circularAssign = await _cir.CIR_ToAck_Vsl.AsNoTracking()
            .Where(a => (ids.Contains(a.VesselID) || a.VesselID == -1) && (a.IsActive ?? 1) == 1)
            .Select(a => new { a.VesselID, a.CIR_MasterID, a.UserRank })
            .ToListAsync();
        foreach (var a in circularAssign)
            AddRank(catalog.CircularAssign, a.VesselID, a.CIR_MasterID, a.UserRank);
        foreach (var f in fullCopy.Where(f => f.Type == TypeCircular))
            AddRank(catalog.CircularAssign, f.VesselId, f.Id, null);

        var circularIds = circularAssign.Select(a => a.CIR_MasterID)
            .Concat(fullCopy.Where(f => f.Type == TypeCircular).Select(f => f.Id))
            .Distinct().ToList();
        if (circularIds.Count > 0)
        {
            var masters = await _cir.CIR_Master.AsNoTracking()
                .Where(c => circularIds.Contains(c.CIR_MasterID) && (c.IsDeleted ?? 0) != 1
                         && c.Status != "Temp" && c.Status != "Draft")
                .Select(c => new
                {
                    c.CIR_MasterID, c.CIR_CategoryID, c.CircularType, c.CIR_Number, c.Reference, c.Title, c.Status,
                    c.Priority, c.DateIssued, c.Remarks, c.ReleasedDate, c.ModifiedDate,
                })
                .ToListAsync();
            var blobs = (await _cir.CIR_Blob.AsNoTracking()
                    .Where(b => b.CIR_MasterID != null && circularIds.Contains(b.CIR_MasterID.Value))
                    .Select(b => new { b.CIR_BlobID, CIR_MasterID = b.CIR_MasterID!.Value, b.BlobSize, b.FileName, b.FileType })
                    .ToListAsync())
                .ToLookup(b => b.CIR_MasterID);

            foreach (var c in masters)
            {
                var files = string.Join(",", blobs[c.CIR_MasterID].OrderBy(b => b.CIR_BlobID)
                    .Select(b => $"{b.CIR_BlobID}:{b.BlobSize}:{b.FileName}:{b.FileType}"));
                var baseKey = string.Join("|", c.CIR_CategoryID, c.CircularType, c.CIR_Number, c.Reference, c.Title, c.Status,
                    c.Priority, Ticks(c.DateIssued), c.Remarks, Ticks(c.ReleasedDate), Ticks(c.ModifiedDate), files);
                // Alerts first, then by priority (1 High, 2 Medium, 3 Low).
                var isAlert = (c.CircularType ?? "").Trim().StartsWith("Alert", StringComparison.OrdinalIgnoreCase);
                var order = (isAlert ? 0 : 10) + Math.Clamp(c.Priority ?? 3, 1, 9);
                catalog.Circulars[c.CIR_MasterID] = new CircularMeta(c.CIR_MasterID, baseKey, order);
            }
        }

        return catalog;
    }

    private bool? _fullCopyInstalled;

    /// <summary>Rows of dbo.Sync_FullCopyItem for these vessels (empty when 008_Sync_FullCopy.sql has not been run).</summary>
    private async Task<List<(int VesselId, string Type, long Id)>> LoadFullCopyAsync(List<int> vesselIds)
    {
        var rows = new List<(int, string, long)>();
        if (vesselIds.Count == 0) return rows;
        _fullCopyInstalled ??= await _general.TableExistsAsync("Sync_FullCopyItem");
        if (!_fullCopyInstalled.Value) return rows;

        var ids = vesselIds.Distinct().ToList();
        var items = await _general.SyncFullCopyItems.AsNoTracking()
            .Where(f => ids.Contains(f.VesselID))
            .Select(f => new { f.VesselID, f.ItemType, f.ItemID })
            .ToListAsync();
        rows.AddRange(items.Select(f => (f.VesselID, f.ItemType.Trim(), f.ItemID)));
        return rows;
    }

    private static void AddRank(Dictionary<int, Dictionary<long, SortedSet<string>>> map, int vesselId, long itemId, string? rank)
    {
        if (!map.TryGetValue(vesselId, out var byItem)) map[vesselId] = byItem = new();
        if (!byItem.TryGetValue(itemId, out var ranks)) byItem[itemId] = ranks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(rank)) ranks.Add(rank!.Trim());
    }

    // =========================================================================================
    // Full details for the items being sent
    // =========================================================================================

    /// <summary>The manuals being sent, as office rows: DM_Manual, its released DM_ManualVersion, DM_Manual_Attachments
    /// (without the file contents), DM_Manual_ToAck_Vsl for this vessel or -1, and this vessel's DM_Manual_UserAck.</summary>
    private async Task<List<SyncManualDto>> LoadManualsAsync(Catalog catalog, List<long> manualIds, int vesselId)
    {
        if (manualIds.Count == 0) return new();

        var manuals = await _dms.DmManuals.AsNoTracking()
            .Where(m => manualIds.Contains(m.DM_ManualID) && (m.IsDeleted ?? 0) != 1)
            .Select(m => new SyncDMManualRow
            {
                DM_ManualID = m.DM_ManualID,
                Origin = m.Origin,
                VesselID = m.VesselID,
                DM_CategoryID = m.DM_CategoryID,
                ManualNo = m.ManualNo,
                ManualCode = m.ManualCode,
                ManualName = m.ManualName,
                DM_StatusID = m.DmStatusId,
                ParentId = m.ParentId,
                ManualLevel = m.ManualLevel,
                IsMainTitle = m.IsMainTitle,
                IsAcknowledgementRequired = m.IsAcknowledgementRequired,
                CanExport = m.CanExport,
                IsApprovalRequired = m.IsApprovalRequired,
                Approval_FlowID = m.Approval_FlowID,
                NextApproval_HQRole = m.NextApproval_HQRole,
                NextApproval_HQDept = m.NextApproval_HQDept,
                SortOrder = m.SortOrder,
                AddlSortOrder = m.AddlSortOrder,
                CreatedBy = m.CreatedBy,
                CreatedDate = m.CreatedDate,
                ModifiedBy = m.ModifiedBy,
                ModifiedDate = m.ModifiedDate,
                IsDeleted = m.IsDeleted,
            })
            .ToListAsync();
        var versionIds = manualIds
            .Select(id => catalog.Manuals.TryGetValue(id, out var meta) ? meta.VersionId : null)
            .Where(v => v != null).Select(v => v!.Value).ToList();
        var versions = (await _dms.DmManualVersions.AsNoTracking()
                .Where(v => versionIds.Contains(v.DM_ManualVersionID))
                .Select(v => new SyncDMManualVersionRow
            {
                DM_ManualVersionID = v.DM_ManualVersionID,
                Origin = v.Origin,
                VesselID = v.VesselID,
                DM_ManualID = v.DM_ManualID,
                Version = v.Version,
                TextContents = v.TextContents,
                PublishedBy = v.PublishedBy,
                PublishedDate = v.PublishedDate,
                CreatedBy = v.CreatedBy,
                CreatedDate = v.CreatedDate,
                ReleasedBy = v.ReleasedBy,
                ReleasedDate = v.ReleasedDate,
                IsActive = v.IsActive,
                IsDisabled = v.IsDisabled,
                LastModifiedBy = v.LastModifiedBy,
                LastModifiedDate = v.LastModifiedDate,
                Comments = v.Comments,
            })
                .ToListAsync())
            .ToDictionary(v => v.DM_ManualID);
        var attachments = (await _dms.DmManualAttachments.AsNoTracking()
                .Where(a => manualIds.Contains(a.DM_ManualID) && (a.IsDeleted ?? 0) != 1)
                .Select(a => new SyncDMManualAttachmentsRow
            {
                DM_Manual_AttachmentsID = a.DmManualAttachmentsId,
                Origin = a.Origin,
                VesselID = a.VesselID,
                DM_ManualID = a.DM_ManualID,
                ImageRefNo = a.ImageRefNo,
                BlobSize = a.BlobSize,
                FileType = a.FileType,
                AddedOn = a.AddedOn,
                IsDeleted = a.IsDeleted,
            })
                .ToListAsync())
            .ToLookup(a => a.DM_ManualID);
        var toAck = (await _dms.DmManualToAckVsls.AsNoTracking()
                .Where(a => manualIds.Contains(a.DM_ManualID) && (a.VesselID == vesselId || a.VesselID == -1))
                .Select(a => new SyncDMManualToAckVslRow
            {
                DM_Manual_ToAck_VslID = a.DmManualToAckVslId,
                Origin = a.Origin,
                VesselID = a.VesselID,
                DM_ManualID = a.DM_ManualID,
                UserRank = a.UserRank,
                IsActive = a.IsActive,
            })
                .ToListAsync())
            .ToLookup(a => a.DM_ManualID);
        var userAcks = (await _dms.DmManualUserAcks.AsNoTracking()
                .Where(a => a.VesselID == vesselId && versionIds.Contains(a.DM_ManualVersionID))
                .Select(a => new SyncDMManualUserAckRow
            {
                DM_Manual_UserAckID = a.DmManualUserAckId,
                Origin = a.Origin,
                VesselID = a.VesselID,
                DM_ManualID = a.DM_ManualID,
                DM_ManualVersionID = a.DM_ManualVersionID,
                Version = a.Version,
                UserRank = a.UserRank,
                UserName = a.UserName,
                UserID = a.UserId,
                DateRead = a.DateRead,
                Comments = a.Comments,
                IsActive = a.IsActive,
            })
                .ToListAsync())
            .ToLookup(a => a.DM_ManualID);

        return manuals.Select(m => new SyncManualDto
        {
            DM_ManualID = m.DM_ManualID,
            Manual = m,
            Version = versions.TryGetValue(m.DM_ManualID, out var v) ? v : null,
            Attachments = attachments[m.DM_ManualID].ToList(),
            ToAck = toAck[m.DM_ManualID].ToList(),
            UserAcks = userAcks[m.DM_ManualID].ToList(),
        }).ToList();
    }

    /// <summary>The circulars being sent, as office rows: CIR_Master, CIR_Blob (without the file contents),
    /// CIR_ToAck_Vsl for this vessel or -1, and this vessel's CIR_UserAck.</summary>
    private async Task<List<SyncCircularDto>> LoadCircularsAsync(List<long> circularIds, int vesselId)
    {
        if (circularIds.Count == 0) return new();

        var masters = await _cir.CIR_Master.AsNoTracking()
            .Where(c => circularIds.Contains(c.CIR_MasterID) && (c.IsDeleted ?? 0) != 1)
            .Select(c => new SyncCIRMasterRow
            {
                CIR_MasterID = c.CIR_MasterID,
                Origin = c.Origin,
                VesselID = c.VesselID,
                CircularType = c.CircularType,
                CIR_CategoryID = c.CIR_CategoryID,
                CIR_Number = c.CIR_Number,
                Reference = c.Reference,
                Title = c.Title,
                Status = c.Status,
                Priority = c.Priority,
                DateIssued = c.DateIssued,
                Remarks = c.Remarks,
                ReleasedDate = c.ReleasedDate,
                CreatedBy = c.CreatedBy,
                CreatedDate = c.CreatedDate,
                ModifiedBy = c.ModifiedBy,
                ModifiedDate = c.ModifiedDate,
                IsDeleted = c.IsDeleted,
            })
            .ToListAsync();
        var blobs = (await _cir.CIR_Blob.AsNoTracking()
                .Where(b => b.CIR_MasterID != null && circularIds.Contains(b.CIR_MasterID.Value))
                .Select(b => new SyncCIRBlobRow
            {
                CIR_BlobID = b.CIR_BlobID,
                Origin = b.Origin,
                VesselID = b.VesselID,
                CIR_MasterID = b.CIR_MasterID,
                Description = b.Description,
                FileName = b.FileName,
                FileType = b.FileType,
                OriginalFileType = b.OriginalFileType,
                BlobSize = b.BlobSize,
                AddedOn = b.AddedOn,
                Cloud_ContainerName = b.Cloud_ContainerName,
                Cloud_Directory = b.Cloud_Directory,
                HQ_Storage_Option = b.HQ_Storage_Option,
                HQ_DatabaseName = b.HQ_DatabaseName,
                HQ_Local_Folder = b.HQ_Local_Folder,
                VSL_Storage_Option = b.VSL_Storage_Option,
                VSL_DatabaseName = b.VSL_DatabaseName,
                VSL_Local_Folder = b.VSL_Local_Folder,
            })
                .ToListAsync())
            .ToLookup(b => b.CIR_MasterID ?? 0);
        var toAck = (await _cir.CIR_ToAck_Vsl.AsNoTracking()
                .Where(a => circularIds.Contains(a.CIR_MasterID) && (a.VesselID == vesselId || a.VesselID == -1))
                .Select(a => new SyncCIRToAckVslRow
            {
                CIR_ToAck_VslID = a.CIR_ToAck_VslID,
                Origin = a.Origin,
                VesselID = a.VesselID,
                CIR_MasterID = a.CIR_MasterID,
                UserRank = a.UserRank,
                IsActive = a.IsActive,
            })
                .ToListAsync())
            .ToLookup(a => a.CIR_MasterID);
        var userAcks = (await _cir.CIR_UserAck.AsNoTracking()
                .Where(a => a.VesselID == vesselId && a.CIR_MasterID != null && circularIds.Contains(a.CIR_MasterID.Value))
                .Select(a => new SyncCIRUserAckRow
            {
                CIR_UserAckID = a.CIR_UserAckID,
                Origin = a.Origin,
                VesselID = a.VesselID,
                CIR_MasterID = a.CIR_MasterID,
                Fleet_CrewID = a.Fleet_CrewID,
                DateRead = a.DateRead,
                Comments = a.Comments,
                IsActive = a.IsActive,
            })
                .ToListAsync())
            .ToLookup(a => a.CIR_MasterID ?? 0);

        return masters.Select(c => new SyncCircularDto
        {
            CIR_MasterID = c.CIR_MasterID,
            Master = c,
            Blobs = blobs[c.CIR_MasterID].ToList(),
            ToAck = toAck[c.CIR_MasterID].ToList(),
            UserAcks = userAcks[c.CIR_MasterID].ToList(),
        }).ToList();
    }

    private async Task<List<SyncCrewDto>> LoadCrewAsync(int vesselId)
    {
        var today = DateTime.UtcNow.Date;
        var staff = await _general.VslStaffs.AsNoTracking()
            .Where(s => s.VesselID == vesselId)
            .Select(s => new { s.VslStaffId, s.UserId, s.UserName, s.UserRank, s.UserDept, s.IsDisabled, s.LeavingDate, s.Password })
            .ToListAsync();

        return staff
            .Where(s => !string.IsNullOrWhiteSpace(s.UserId))
            .GroupBy(s => s.UserId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(s => new SyncCrewDto
            {
                VslStaffId = s.VslStaffId,
                UserId = s.UserId.Trim(),
                UserName = s.UserName,
                UserRank = s.UserRank,
                UserDept = s.UserDept,
                // Signed off: cannot log in on board any more.
                IsDisabled = s.IsDisabled == 1 || (s.LeavingDate != null && s.LeavingDate.Value.Date < today) ? 1 : 0,
                Password = s.Password,
            })
            .ToList();
    }

    private sealed record FixedTables(List<SyncDmStatusDto> DmStatuses, List<SyncDmSettingDto> DmSettings, List<SyncCirSettingDto> CirSettings, List<SyncVesselDto> Vessels);

    /// <summary>The fixed tables, every row (no filter), ordered by key so the version key is stable.</summary>
    private async Task<FixedTables> LoadFixedTablesAsync()
    {
        var dmStatuses = (await _dms.DmStatuses.AsNoTracking().OrderBy(x => x.DmStatusId).ToListAsync())
            .Select(x => new SyncDmStatusDto
            {
                DM_StatusID = x.DmStatusId,
                Origin = x.Origin?.TrimEnd() ?? string.Empty,
                VesselID = x.VesselID,
                StatusCode = x.StatusCode?.TrimEnd(),
                StatusName = x.StatusName?.TrimEnd() ?? string.Empty,
                IsDisabled = x.IsDisabled,
                IsVisible = x.IsVisible,
                SortOrder = x.SortOrder,
            })
            .ToList();
        var dmSettings = (await _dms.DmSettings.AsNoTracking().OrderBy(x => x.DmSettingsId).ToListAsync())
            .Select(x => new SyncDmSettingDto
            {
                DM_SettingsID = x.DmSettingsId,
                Origin = x.Origin?.TrimEnd() ?? string.Empty,
                VesselID = x.VesselID,
                SettingName = x.SettingName?.TrimEnd() ?? string.Empty,
                SettingValue = x.SettingValue?.TrimEnd(),
            })
            .ToList();
        var cirSettings = (await _cir.CIR_Settings.AsNoTracking().OrderBy(x => x.CIR_SettingsID).ToListAsync())
            .Select(x => new SyncCirSettingDto
            {
                CIR_SettingsID = x.CIR_SettingsID,
                Origin = x.Origin?.TrimEnd() ?? string.Empty,
                VesselID = x.VesselID,
                SettingName = x.SettingName?.TrimEnd() ?? string.Empty,
                SettingValue = x.SettingValue?.TrimEnd(),
            })
            .ToList();
        // TableIDstart (start of each vessel's ID range for Proc_NextID) goes along when V_Vessel has that column
        // (dbo.Api_VesselIdRanges returns NULLs when it does not).
        var idStart = (await _general.Database.SqlQuery<VesselIdRangeRow>($"EXEC dbo.Api_VesselIdRanges").ToListAsync())
            .GroupBy(r => r.VesselID).ToDictionary(g => g.Key, g => g.First().TableIDstart);
        var vessels = (await _general.VVessels.AsNoTracking().OrderBy(x => x.VVesselId).ToListAsync())
            .Select(x => new SyncVesselDto
            {
                V_VesselID = x.VVesselId,
                Origin = x.Origin?.TrimEnd() ?? string.Empty,
                VesselID = x.VesselID,
                VslCode = x.VslCode?.TrimEnd() ?? string.Empty,
                VslName = x.VslName?.TrimEnd() ?? string.Empty,
                VslType = x.VslType?.TrimEnd(),
                DelivDate = x.DelivDate,
                BaseCurrency = x.BaseCurrency?.TrimEnd(),
                VslEmailAddress = x.VslEmailAddress?.TrimEnd(),
                OperateBy = x.OperateBy?.TrimEnd(),
                IsRetired = x.IsRetired,
                TableIDstart = idStart.GetValueOrDefault(x.VesselID),
            })
            .ToList();
        return new FixedTables(dmStatuses, dmSettings, cirSettings, vessels);
    }

    private async Task<(List<SyncManualCategoryDto>, List<SyncCircularCategoryDto>)> LoadCategoriesAsync()
    {
        // Every row, disabled ones too: a manual or circular on board may still be filed under one.
        var manual = await _dms.DmCategories.AsNoTracking()
            .Select(c => new SyncManualCategoryDto
            {
                DM_CategoryID = c.DM_CategoryID, Origin = c.Origin, VesselID = c.VesselID,
                CategoryCode = c.CategoryCode, CategoryName = c.CategoryName, IsDisabled = c.IsDisabled,
            })
            .ToListAsync();
        var circular = await _cir.CIR_Category.AsNoTracking()
            .Select(c => new SyncCircularCategoryDto
            {
                CIR_CategoryID = c.CIR_CategoryID, Origin = c.Origin, VesselID = c.VesselID, CircularType = c.CircularType,
                CategoryCode = c.CategoryCode, Category = c.Category, isActive = c.isActive, Sortorder = c.Sortorder,
                CategoryName = c.Category,
            })
            .ToListAsync();
        return (manual, circular);
    }

    // =========================================================================================
    // Sync_VesselItem, Sync_Vessel, Sync_Log
    // =========================================================================================

    private sealed record TrackedRow(int VesselId, string Type, long Id, string? Pending, string? Confirmed);

    /// <summary>Tracked items of one vessel (or all vessels when null).</summary>
    private async Task<List<TrackedRow>> LoadTrackedRowsAsync(int? vesselId)
    {
        var query = _general.SyncVesselItems.AsNoTracking();
        if (vesselId != null) query = query.Where(i => i.VesselID == vesselId.Value);
        var rows = await query.Select(i => new { i.VesselID, i.ItemType, i.ItemID, i.PendingKey, i.ConfirmedKey }).ToListAsync();
        return rows.Select(r => new TrackedRow(r.VesselID, r.ItemType.Trim(), r.ItemID, r.PendingKey?.Trim(), r.ConfirmedKey?.Trim())).ToList();
    }

    private async Task<Dictionary<(string Type, long Id), TrackedRow>> LoadTrackedAsync(int vesselId) =>
        (await LoadTrackedRowsAsync(vesselId)).ToDictionary(t => (t.Type, t.Id));

    private async Task<bool> IsTrackedAsync(int vesselId, string type, long id)
    {
        return await _general.SyncVesselItems.AsNoTracking()
            .AnyAsync(i => i.VesselID == vesselId && i.ItemType == type && i.ItemID == id && (i.PendingKey ?? "") != DeletedKey);
    }

    private async Task MarkPendingAsync(int vesselId, List<(string Type, long Id, string Key)> rows, DateTime serverTime)
    {
        // Load-or-add per chunk; one SaveChanges per chunk.
        foreach (var chunk in rows.GroupBy(r => (r.Type, r.Id)).Select(g => g.Last()).Chunk(500))
        {
            var itemIds = chunk.Select(c => c.Id).Distinct().ToList();
            var existing = (await _general.SyncVesselItems
                    .Where(i => i.VesselID == vesselId && itemIds.Contains(i.ItemID))
                    .ToListAsync())
                .GroupBy(i => (i.ItemType.Trim(), i.ItemID))
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var (type, id, key) in chunk)
            {
                if (!existing.TryGetValue((type, id), out var item))
                    _general.SyncVesselItems.Add(item = new SyncVesselItem { VesselID = vesselId, ItemType = type, ItemID = id });
                item.PendingKey = key;
                item.PendingUtc = serverTime;
            }
            await _general.SaveChangesAsync();
            _general.ChangeTracker.Clear();
        }
    }

    /// <summary>The Sync_Vessel row of a vessel, tracked (a new one when the vessel has none yet).</summary>
    private async Task<SyncVessel> SyncVesselRowAsync(int vesselId)
    {
        var row = await _general.SyncVessels.FirstOrDefaultAsync(v => v.VesselID == vesselId);
        if (row == null) _general.SyncVessels.Add(row = new SyncVessel { VesselID = vesselId, PendingOnShip = 0 });
        return row;
    }

    /// <summary>Saves a Sync_Vessel row; when another call added it at the same moment, applies the change to that one.</summary>
    private async Task SaveSyncVesselAsync(SyncVessel row, SyncLog? log = null)
    {
        if (log != null) _general.SyncLogs.Add(log);
        try
        {
            await _general.SaveChangesAsync();
        }
        catch (DbUpdateException) when (_general.Entry(row).State == EntityState.Added)
        {
            // The row was added by a parallel call: update that one instead.
            var values = _general.Entry(row).CurrentValues.Clone();
            _general.Entry(row).State = EntityState.Detached;
            var stored = await _general.SyncVessels.FirstAsync(v => v.VesselID == row.VesselID);
            _general.Entry(stored).CurrentValues.SetValues(values);
            await _general.SaveChangesAsync();
        }
        finally
        {
            _general.ChangeTracker.Clear();
        }
    }

    private async Task RecordCallAsync(int vesselId, string direction, DateTime startedUtc, int items, string? error)
    {
        var ok = error == null;
        var now = DateTime.UtcNow;
        var row = await SyncVesselRowAsync(vesselId);
        row.LastContactUtc = now;
        if (ok) row.LastSuccessUtc = now;
        if (direction == "Down") row.LastPullUtc = now;
        if (direction == "Up") row.LastPushUtc = now;
        row.LastStatus = ok ? "OK" : "Failed";
        row.LastError = Truncate(error, 1000);
        await SaveSyncVesselAsync(row, new SyncLog
        {
            VesselID = vesselId,
            Direction = direction,
            StartedUtc = startedUtc,
            FinishedUtc = now,
            ItemsCount = items,
            Status = ok ? "OK" : "Failed",
            Error = Truncate(error, 1000),
        });
    }

    private async Task TryRecordCallAsync(int vesselId, string direction, DateTime startedUtc, string error)
    {
        try { await RecordCallAsync(vesselId, direction, startedUtc, 0, error); }
        catch { /* the original error is what matters */ }
    }

    // =========================================================================================
    // Helpers
    // =========================================================================================

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string HashKey(string key) => Hash(key.Trim());

    private static long? Ticks(DateTime? value) => value?.Ticks;

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static int? LeadingNumber(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var digits = new string(version.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    private static string? Truncate(string? value, int max) =>
        value == null ? null : value.Length <= max ? value : value[..max];
}
