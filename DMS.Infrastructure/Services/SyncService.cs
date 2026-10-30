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
/// Office side of the vessel sync (api/Sync/...), checked with the vessel key (Sync_VesselKey):
///   Changes / ChangesConfirm   office -> vessel: the records waiting in Sync_ChangeLog, each as it is now
///   Push                       vessel -> office: acknowledgements made on board
///   OtherDocument / PushRows   vessel -> office: documents and other records changed on board (Ship_ChangeLog)
///   Heartbeat                  the vessel's link state and progress (Sync_Vessel)
///   ...Attachment / ...File    file contents, downloaded by the vessel during its sync
/// Every call is recorded in Sync_Log.
///
/// The vessel monitor's "Queued" / "On board" comes from the changes still waiting in Sync_ChangeLog.
/// </summary>
public partial class SyncService : ISyncService
{
    private const string TypeManual = "Manual";
    private const string TypeCircular = "Circular";

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
        foreach (var table in new[] { "Sync_VesselKey", "Sync_Vessel", "Sync_Log" })
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
        if (row == null)
            _general.SyncVesselKeys.Add(row = new SyncVesselKey
            {
                Sync_VesselKeyID = await _general.NextIdAsync<SyncVesselKey>(),
                Origin = TableIdExtensions.OriginHQ,
                VesselID = vesselId,
            });
        row.KeyHash = HashKey(key);
        row.IsActive = true;
        row.CreatedUtc = DateTime.UtcNow;
        row.CreatedBy = Truncate(createdBy, 150);
        row.LastUsedUtc = null;
        await _general.SaveChangesAsync();
        _general.Entry(row).State = EntityState.Detached;

        return key;
    }

    /// <summary>
    /// "Resync": every record the vessel should hold is queued for it again (dbo.Sync_QueueFullCopy writes a
    /// Sync_ChangeLog row per record not already waiting); its next sync brings them all, as they are now.
    /// </summary>
    public async Task RequestResyncAsync(int vesselId)
    {
        if (vesselId <= 0) throw new ArgumentOutOfRangeException(nameof(vesselId), "Give one vessel.");
        _general.Database.SetCommandTimeout(TimeSpan.FromMinutes(10)); // the whole library for one vessel
        await _general.Database.ExecuteSqlInterpolatedAsync($"EXEC dbo.Sync_QueueFullCopy @VesselID = {vesselId}");
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
                    .OrderBy(c => c.Sync_ChangeLogID)
                    .Take(max + 1)
                    .Select(c => new { c.Sync_ChangeLogID, c.TableName, c.KeyValue })
                    .ToListAsync())
                .Select(c => (Id: c.Sync_ChangeLogID, Table: c.TableName, Key: c.KeyValue))
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
        await PruneAsync(vesselId);
        return new SyncChangesConfirmResult { Confirmed = confirmed };
    }

    /// <summary>Days a change the vessel has confirmed is kept in Sync_ChangeLog (then deleted).</summary>
    private const int ChangeLogKeepDays = 30;
    /// <summary>Days a sync call is kept in Sync_Log (the Sync history of the vessel).</summary>
    private const int SyncLogKeepDays = 90;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> LastPruneUtc = new();

    /// <summary>
    /// Keeps Sync_ChangeLog and Sync_Log from growing for ever: a vessel's confirmed changes older than
    /// ChangeLogKeepDays and its calls older than SyncLogKeepDays are deleted, at most once an hour per vessel.
    /// Never fails the sync call it runs in.
    /// </summary>
    private async Task PruneAsync(int vesselId)
    {
        var now = DateTime.UtcNow;
        if (LastPruneUtc.TryGetValue(vesselId, out var last) && now - last < TimeSpan.FromHours(1)) return;
        LastPruneUtc[vesselId] = now;
        try
        {
            var changesBefore = now.AddDays(-ChangeLogKeepDays);
            await _general.SyncChangeLogs
                .Where(c => c.VesselID == vesselId && c.DownloadedUtc != null && c.DownloadedUtc < changesBefore)
                .ExecuteDeleteAsync();
            var callsBefore = now.AddDays(-SyncLogKeepDays);
            await _general.Sync_Logs
                .Where(l => l.VesselID == vesselId && l.StartedUtc < callsBefore)
                .ExecuteDeleteAsync();
        }
        catch
        {
            LastPruneUtc.TryRemove(vesselId, out _); // tried again with the next call
        }
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
            var rows = _general.SyncChangeLogs.Where(c => c.VesselID == vesselId && list.Contains(c.Sync_ChangeLogID));
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
                BlobSize = b.BlobSize ?? 0,
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
                DocumentNumber = m.DocumentNumber ?? "",
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
        var pending = Math.Max(0, request.PendingManualAcks) + Math.Max(0, request.PendingCircularAcks) + Math.Max(0, request.PendingOther);
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

    // The file downloads below answer in three ways:
    //   null           no such file for this vessel (removed, or another vessel's): 404
    //   empty Bytes    the record is there but the office has no file for it (none was ever added): 204, so the
    //                  ship stops asking for it
    //   Bytes          the file

    public async Task<(byte[] Bytes, string FileType, string? FileName)?> GetManualAttachmentBytesAsync(int vesselId, long attachmentId)
    {
        var a = await _dms.DmManualAttachments.AsNoTracking()
            .Where(x => x.DmManualAttachmentsId == attachmentId && (x.IsDeleted ?? 0) != 1)
            .Select(x => new { x.VesselID, x.BlobContents, x.FileType })
            .FirstOrDefaultAsync();
        if (a == null) return null;
        if (!IsForVessel(a.VesselID, vesselId)) return null; // another vessel's file
        // Manual files are only kept in the database: no contents there means there is no file.
        return (a.BlobContents is { Length: > 0 } ? a.BlobContents : Array.Empty<byte>(), a.FileType, null);
    }

    public async Task<(byte[] Bytes, string FileType, string? FileName)?> GetCircularAttachmentBytesAsync(int vesselId, long attachmentId)
    {
        var b = await _cir.CIR_Blob.AsNoTracking()
            .Where(x => x.CIR_BlobID == attachmentId)
            .Select(x => new { x.CIR_MasterID, x.VesselID, x.BlobContents, x.FileType, x.FileName,
                               x.HQ_Storage_Option, x.HQ_Local_Folder, x.Cloud_ContainerName, x.Cloud_Directory })
            .FirstOrDefaultAsync();
        if (b == null || b.CIR_MasterID == null) return null;
        if (!IsForVessel(b.VesselID, vesselId)) return null; // another vessel's file
        // No file was ever added (no name, nothing in the database): nothing to fetch from a folder or the cloud.
        if (string.IsNullOrWhiteSpace(b.FileName) && b.BlobContents is not { Length: > 0 })
            return (Array.Empty<byte>(), b.FileType ?? "application/octet-stream", null);
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
        // No file was ever added (no name, nothing in the database): nothing to fetch from a folder or the cloud.
        if (string.IsNullOrWhiteSpace(f.FileName) && f.BlobContents is not { Length: > 0 })
            return (Array.Empty<byte>(), f.FileType ?? f.OriginalFileType ?? "application/octet-stream", null);
        // In the database, a local folder or the cloud (BlobTableSettings).
        var bytes = await _storage.ReadAsync(AttachmentStorage.OtherDocumentTable, new AttachmentRef(blobId, f.FileName, f.BlobContents,
            f.HQ_Storage_Option, f.HQ_Local_Folder, f.Cloud_ContainerName, f.Cloud_Directory));
        if (bytes == null) return null;
        return (bytes, f.FileType ?? f.OriginalFileType ?? "application/octet-stream", f.FileName);
    }

    // =========================================================================================
    // Sync monitor
    // =========================================================================================

    /// <summary>The synced tables that belong to a manual or a circular (what the monitor counts as "queued").</summary>
    private static readonly HashSet<string> ItemTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "DM_Manual", "DM_ManualVersion", "DM_Manual_Attachments", "DM_Manual_ToAck_Vsl",
        "CIR_Master", "CIR_Blob", "CIR_ToAck_Vsl",
    };

    /// <summary>
    /// Per vessel, the manuals and circulars with a change waiting for it in Sync_ChangeLog (not yet confirmed by the
    /// vessel): the record itself, a version, a file or a vessel assignment of it. Empty when the change log is missing.
    /// </summary>
    public async Task<Dictionary<int, HashSet<(string ItemType, long ItemId)>>> GetQueuedItemsAsync(IReadOnlyCollection<int> vesselIds)
    {
        var result = vesselIds.Distinct().ToDictionary(v => v, _ => new HashSet<(string ItemType, long ItemId)>());
        if (result.Count == 0 || !await _general.TableExistsAsync("Sync_ChangeLog")) return result;

        var ids = result.Keys.ToList();
        var tables = ItemTables.ToList();
        var waiting = await _general.SyncChangeLogs.AsNoTracking()
            .Where(c => ids.Contains(c.VesselID) && c.DownloadedUtc == null && tables.Contains(c.TableName))
            .Select(c => new { c.VesselID, c.TableName, c.KeyValue })
            .Distinct()
            .ToListAsync();
        if (waiting.Count == 0) return result;

        List<long> KeysOf(string table) => waiting
            .Where(w => string.Equals(w.TableName.Trim(), table, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.KeyValue).Distinct().ToList();

        // Child records -> their manual / circular (a record deleted since cannot be traced and is not counted).
        // Only the two columns are read (Select before ToDictionaryAsync): without it EF loads whole rows - a
        // version's manual text, an attachment's file - and picks the two values in memory.
        var versionKeys = KeysOf("DM_ManualVersion");
        var versions = versionKeys.Count == 0 ? new Dictionary<long, long>() : await _dms.DmManualVersions.AsNoTracking()
            .Where(v => versionKeys.Contains(v.DM_ManualVersionID))
            .Select(v => new { v.DM_ManualVersionID, v.DM_ManualID })
            .ToDictionaryAsync(v => v.DM_ManualVersionID, v => v.DM_ManualID);
        var fileKeys = KeysOf("DM_Manual_Attachments");
        var manualFiles = fileKeys.Count == 0 ? new Dictionary<long, long>() : await _dms.DmManualAttachments.AsNoTracking()
            .Where(a => fileKeys.Contains(a.DmManualAttachmentsId))
            .Select(a => new { a.DmManualAttachmentsId, a.DM_ManualID })
            .ToDictionaryAsync(a => a.DmManualAttachmentsId, a => a.DM_ManualID);
        var manualAssignKeys = KeysOf("DM_Manual_ToAck_Vsl");
        var manualAssign = manualAssignKeys.Count == 0 ? new Dictionary<long, long>() : await _dms.DmManualToAckVsls.AsNoTracking()
            .Where(a => manualAssignKeys.Contains(a.DmManualToAckVslId))
            .Select(a => new { a.DmManualToAckVslId, a.DM_ManualID })
            .ToDictionaryAsync(a => a.DmManualToAckVslId, a => a.DM_ManualID);
        var blobKeys = KeysOf("CIR_Blob");
        var circularFiles = blobKeys.Count == 0 ? new Dictionary<long, long>() : (await _cir.CIR_Blob.AsNoTracking()
                .Where(b => blobKeys.Contains(b.CIR_BlobID) && b.CIR_MasterID != null)
                .Select(b => new { b.CIR_BlobID, b.CIR_MasterID })
                .ToListAsync())
            .ToDictionary(b => b.CIR_BlobID, b => b.CIR_MasterID!.Value);
        var circularAssignKeys = KeysOf("CIR_ToAck_Vsl");
        var circularAssign = circularAssignKeys.Count == 0 ? new Dictionary<long, long>() : await _cir.CIR_ToAck_Vsl.AsNoTracking()
            .Where(a => circularAssignKeys.Contains(a.CIR_ToAck_VslID))
            .Select(a => new { a.CIR_ToAck_VslID, a.CIR_MasterID })
            .ToDictionaryAsync(a => a.CIR_ToAck_VslID, a => a.CIR_MasterID);

        foreach (var w in waiting)
        {
            var queued = result[w.VesselID];
            switch (w.TableName.Trim().ToUpperInvariant())
            {
                case "DM_MANUAL": queued.Add((TypeManual, w.KeyValue)); break;
                case "DM_MANUALVERSION" when versions.TryGetValue(w.KeyValue, out var m): queued.Add((TypeManual, m)); break;
                case "DM_MANUAL_ATTACHMENTS" when manualFiles.TryGetValue(w.KeyValue, out var m): queued.Add((TypeManual, m)); break;
                case "DM_MANUAL_TOACK_VSL" when manualAssign.TryGetValue(w.KeyValue, out var m): queued.Add((TypeManual, m)); break;
                case "CIR_MASTER": queued.Add((TypeCircular, w.KeyValue)); break;
                case "CIR_BLOB" when circularFiles.TryGetValue(w.KeyValue, out var c): queued.Add((TypeCircular, c)); break;
                case "CIR_TOACK_VSL" when circularAssign.TryGetValue(w.KeyValue, out var c): queued.Add((TypeCircular, c)); break;
            }
        }
        return result;
    }

    /// <summary>Per vessel, the changes (records of any synced table) waiting in Sync_ChangeLog for its next sync.</summary>
    public async Task<Dictionary<int, int>> GetWaitingChangesAsync(IReadOnlyCollection<int> vesselIds)
    {
        var ids = vesselIds.Distinct().ToList();
        if (ids.Count == 0 || !await _general.TableExistsAsync("Sync_ChangeLog")) return new Dictionary<int, int>();
        return await _general.SyncChangeLogs.AsNoTracking()
            .Where(c => ids.Contains(c.VesselID) && c.DownloadedUtc == null)
            .GroupBy(c => c.VesselID)
            .Select(g => new { VesselID = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VesselID, x => x.Count);
    }

    // =========================================================================================
    // Sync_Vessel, Sync_Log
    // =========================================================================================

    /// <summary>The Sync_Vessel row of a vessel, tracked (a new one when the vessel has none yet).</summary>
    private async Task<SyncVessel> SyncVesselRowAsync(int vesselId)
    {
        var row = await _general.SyncVessels.FirstOrDefaultAsync(v => v.VesselID == vesselId);
        if (row == null)
            _general.SyncVessels.Add(row = new SyncVessel
            {
                Sync_VesselID = await _general.NextIdAsync<SyncVessel>(),
                Origin = TableIdExtensions.OriginHQ,
                VesselID = vesselId,
                PendingOnShip = 0,
            });
        return row;
    }

    /// <summary>Saves a Sync_Vessel row; when another call added it at the same moment, applies the change to that one.</summary>
    private async Task SaveSyncVesselAsync(SyncVessel row, Sync_Log? log = null)
    {
        if (log != null) _general.Sync_Logs.Add(log);
        try
        {
            await _general.SaveChangesAsync();
        }
        catch (DbUpdateException) when (_general.Entry(row).State == EntityState.Added)
        {
            // The row was added by a parallel call: update that one instead. Any other failure (no parallel row)
            // is thrown as it is, so the real database error shows - not "Sequence contains no elements".
            var values = _general.Entry(row).CurrentValues.Clone();
            _general.Entry(row).State = EntityState.Detached;
            var stored = await _general.SyncVessels.FirstOrDefaultAsync(v => v.VesselID == row.VesselID);
            if (stored == null) throw;
            values[nameof(SyncVessel.Sync_VesselID)] = stored.Sync_VesselID; // keep that row's key
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
        await SaveSyncVesselAsync(row, new Sync_Log
        {
            Sync_LogID = await _general.NextIdAsync<Sync_Log>(),
            Origin = TableIdExtensions.OriginHQ,
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

    /// <summary>A record goes to a vessel when it is that vessel's, or for every vessel (-1, or the old 0); -2 = office only.</summary>
    private static bool IsForVessel(int recordVesselId, int vesselId) => recordVesselId is -1 or 0 || recordVesselId == vesselId;

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
