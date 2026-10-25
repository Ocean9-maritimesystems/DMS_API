using DMS.Application.DTO;
using DMS.Application.Interface;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Application;

public class VesselService : IVesselService
{
    private readonly GeneralContext _dbContext;
    private readonly IMediator _mediator;
    private readonly ManualGeneralQueries _queries;
    private readonly DmsContext _dms;
    private readonly CircularsContext _circulars;
    private readonly ISyncService _sync;

    public VesselService(GeneralContext context, IMediator mediator, ManualGeneralQueries queries,
                         DmsContext dms, CircularsContext circulars, ISyncService sync)
    {
        _dms = dms;
        _circulars = circulars;
        _sync = sync;
        _dbContext = context;
        _mediator = mediator;
        _queries = queries;
    }

    public async Task<List<string>> GetVslTypeList()
    {
        var vslTypeList = await _dbContext.VVessels
                                .Select(x => x.VslType)
                                .Where(vslType => !string.IsNullOrEmpty(vslType))
                                .Distinct()
                                .ToListAsync();

        // Ensure null values are filtered out to match the interface signature
        return vslTypeList.Where(vslType => vslType != null).Cast<string>().ToList();
    }

    public async Task<List<VesselListDto>> GetVesselList()
    {
        var vesselList = await _dbContext.VVessels
                                      .Select(x => new VesselListDto
                                      {
                                          VesselID = x.VesselID,
                                          VslName = x.VslName,
                                          VslCode = x.VslCode,
                                          VslType = x.VslType
                                      }).ToListAsync();
        return vesselList;
    }

    // ---------------------------------------------------------------------------------------------
    // Vessel sync: what has been sent to a vessel, the sync calls, and the fleet monitor.
    // Other documents are not synced to vessels, so they are not listed here.
    // ---------------------------------------------------------------------------------------------

    public async Task<List<VesselSyncItemDto>> GetVesselSyncItemsAsync(int vesselId)
    {
        // Everything is filtered in SQL against the vessel's assignment (EXISTS sub-queries), only the
        // columns shown are read, and results are grouped once in memory. VesselID -1 means "all vessels".
        var items = new List<VesselSyncItemDto>();

        // Items given to this vessel by the one-time full copy (Database/008_Sync_FullCopy.sql),
        // whether or not they are assigned. No ranks acknowledge them, so they show as "Sent".
        var (fullCopyManuals, fullCopyCirculars) = await LoadFullCopyIdsAsync(vesselId);

        // ---- Circulars and alerts ----
        var cirAssigned = _circulars.CIR_ToAck_Vsl.AsNoTracking()
            .Where(a => (a.VesselID == vesselId || a.VesselID == -1) && (a.IsActive ?? 1) == 1);

        var cirRanks = (await cirAssigned.Select(a => new { a.CIR_MasterID, a.UserRank }).ToListAsync())
            .ToLookup(a => a.CIR_MasterID, a => a.UserRank);

        if (cirRanks.Count > 0 || fullCopyCirculars.Count > 0)
        {
            var masters = await _circulars.CIR_Master.AsNoTracking()
                .Where(m => (m.IsDeleted ?? 0) == 0 && m.Status != "Temp" && m.Status != "Draft"
                         && (cirAssigned.Any(a => a.CIR_MasterID == m.CIR_MasterID) || fullCopyCirculars.Contains(m.CIR_MasterID)))
                .Select(m => new { m.CIR_MasterID, m.CIR_Number, m.Title, m.CircularType, m.ReleasedDate, m.DateIssued })
                .ToListAsync();

            var cirReads = (await _circulars.Vw_CIR_VesselAck.AsNoTracking()
                    .Where(x => x.VesselID == vesselId && x.DateRead != null && x.CIR_MasterID != null
                             && (cirAssigned.Any(a => a.CIR_MasterID == x.CIR_MasterID) || fullCopyCirculars.Contains(x.CIR_MasterID.Value)))
                    .Select(x => new { CIR_MasterID = x.CIR_MasterID!.Value, x.UserRank, x.DateRead })
                    .ToListAsync())
                .ToLookup(x => x.CIR_MasterID);

            foreach (var m in masters)
            {
                var read = cirReads[m.CIR_MasterID];
                items.Add(Item(
                    IsAlert(m.CircularType) ? "Alert" : "Circular",
                    m.CIR_MasterID, m.CIR_Number, m.Title, null, null,
                    m.ReleasedDate ?? m.DateIssued, RankSet(cirRanks[m.CIR_MasterID]),
                    read.Select(a => a.UserRank), read.Max(a => a.DateRead)));
            }
        }

        // ---- Manuals with a released version ----
        var manAssigned = _dms.DmManualToAckVsls.AsNoTracking()
            .Where(a => (a.VesselID == vesselId || a.VesselID == -1) && (a.IsActive ?? 1) == 1);

        var manRanks = (await manAssigned.Select(a => new { a.DM_ManualID, a.UserRank }).ToListAsync())
            .ToLookup(a => a.DM_ManualID, a => a.UserRank);

        if (manRanks.Count > 0 || fullCopyManuals.Count > 0)
        {
            // The newest active version of each manual, picked in SQL (NOT EXISTS a newer active one).
            var latestVersions = _dms.DmManualVersions.AsNoTracking()
                .Where(v => v.IsActive == 1
                         && !_dms.DmManualVersions.Any(n => n.DM_ManualID == v.DM_ManualID && n.IsActive == 1
                                                         && n.DM_ManualVersionID > v.DM_ManualVersionID));

            // Manuals without a released version are not sent, so the inner join drops them.
            var manuals = await (
                    from m in _dms.DmManuals.AsNoTracking()
                    join v in latestVersions on m.DM_ManualID equals v.DM_ManualID
                    where (m.IsDeleted ?? 0) == 0
                       && (manAssigned.Any(a => a.DM_ManualID == m.DM_ManualID) || fullCopyManuals.Contains(m.DM_ManualID))
                    select new
                    {
                        m.DM_ManualID, m.ManualNo, m.ManualName, m.CreatedDate,
                        v.DM_ManualVersionID, v.Version, v.ReleasedDate, v.PublishedDate,
                    })
                .ToListAsync();

            // Only acknowledgements of the current version count.
            var manReads = (await _dms.DmManualUserAcks.AsNoTracking()
                    .Where(a => a.VesselID == vesselId && a.DateRead != null
                             && (manAssigned.Any(t => t.DM_ManualID == a.DM_ManualID) || fullCopyManuals.Contains(a.DM_ManualID))
                             && latestVersions.Any(v => v.DM_ManualVersionID == a.DM_ManualVersionID))
                    .Select(a => new { a.DM_ManualID, a.UserRank, a.DateRead })
                    .ToListAsync())
                .ToLookup(a => a.DM_ManualID);

            foreach (var m in manuals)
            {
                var read = manReads[m.DM_ManualID];
                items.Add(Item("Manual", m.DM_ManualID, m.ManualNo, m.ManualName, m.Version, null,
                    m.ReleasedDate ?? m.PublishedDate ?? m.CreatedDate,
                    RankSet(manRanks[m.DM_ManualID]), read.Select(a => a.UserRank), read.Max(a => a.DateRead)));
            }
        }

        // Whether the vessel already has each item (needs the sync tables). Scoped to this one vessel.
        if (items.Count > 0)
        {
            var queuedByVessel = await _sync.GetQueuedItemsAsync(new[] { vesselId });
            if (queuedByVessel.TryGetValue(vesselId, out var queued))
            {
                foreach (var i in items)
                    i.Delivery = queued.Contains((i.ItemType == "Manual" ? "Manual" : "Circular", i.ItemID)) ? "Queued" : "On board";
            }
        }

        return items.OrderByDescending(i => i.IssuedOn ?? DateTime.MinValue).ToList();
    }

    /// <summary>Manual and circular IDs in dbo.Sync_FullCopyItem for the vessel (empty when the table does not exist).</summary>
    private async Task<(List<long> Manuals, List<long> Circulars)> LoadFullCopyIdsAsync(int vesselId)
    {
        var manuals = new List<long>();
        var circulars = new List<long>();
        if (!await _dbContext.TableExistsAsync("Sync_FullCopyItem")) return (manuals, circulars);
        var rows = await _dbContext.SyncFullCopyItems.AsNoTracking()
            .Where(f => f.VesselID == vesselId)
            .Select(f => new { f.ItemType, f.ItemID })
            .ToListAsync();
        foreach (var row in rows)
        {
            var type = row.ItemType.Trim();
            if (type == "Manual") manuals.Add(row.ItemID);
            else if (type == "Circular") circulars.Add(row.ItemID);
        }
        return (manuals, circulars);
    }

    public async Task<VesselSyncHistoryDto> GetVesselSyncHistoryAsync(int vesselId)
    {
        var result = new VesselSyncHistoryDto { SyncInstalled = await _sync.IsInstalledAsync() };
        if (!result.SyncInstalled) return result;

        // The key is extra information: if it cannot be read, the history is still shown (KeyError says why).
        try
        {
            var key = await _dbContext.SyncVesselKeys.AsNoTracking()
                .Where(k => k.VesselID == vesselId && k.IsActive)
                .Select(k => new { k.CreatedUtc, k.CreatedBy, k.LastUsedUtc })
                .FirstOrDefaultAsync();
            if (key != null)
            {
                result.HasKey = true;
                result.KeyCreatedUtc = Utc(key.CreatedUtc);
                result.KeyCreatedBy = key.CreatedBy;
                result.KeyLastUsedUtc = key.LastUsedUtc == null ? null : Utc(key.LastUsedUtc.Value);
            }
        }
        catch (Exception ex)
        {
            result.KeyError = ex.GetBaseException().Message;
        }

        var entries = await _dbContext.SyncLogs.AsNoTracking()
            .Where(l => l.VesselID == vesselId)
            .OrderByDescending(l => l.StartedUtc).ThenByDescending(l => l.SyncLogID)
            .Take(200)
            .ToListAsync();
        result.Entries.AddRange(entries.Select(e => new VesselSyncLogDto
        {
            SyncLogID = e.SyncLogID,
            Direction = e.Direction,
            StartedUtc = Utc(e.StartedUtc),
            FinishedUtc = e.FinishedUtc == null ? null : Utc(e.FinishedUtc.Value),
            ItemsCount = e.ItemsCount,
            Status = e.Status,
            Error = e.Error,
        }));
        return result;
    }

    /// <summary>A vessel is "online" when it contacted the office within this many minutes (vessels check every 5 minutes).</summary>
    private const int OnlineMinutes = 30;

    private sealed record LinkInfo(DateTime? Contact, DateTime? Success, string? Status, string? Error,
                                   int PendingOnShip, string? Activity, int? Progress, string? AppVersion);

    public async Task<VesselSyncMonitorDto> GetSyncMonitorAsync()
    {
        var result = new VesselSyncMonitorDto { OnlineMinutes = OnlineMinutes };

        var vessels = await _dbContext.VVessels.AsNoTracking()
            .Where(v => (v.IsRetired ?? 0) == 0)
            .OrderBy(v => v.VslName)
            .Select(v => new { v.VesselID, v.VslCode, v.VslName, v.VslType, v.VslEmailAddress })
            .ToListAsync();
        var vesselIds = vessels.Select(v => v.VesselID).ToList();

        // ---- What each vessel has been sent (whole fleet, a few queries in total) ----
        var cirToAck = await _circulars.CIR_ToAck_Vsl.AsNoTracking()
            .Where(a => (a.IsActive ?? 1) == 1)
            .Select(a => new { a.CIR_MasterID, a.VesselID, a.UserRank })
            .ToListAsync();
        var cirIds = cirToAck.Select(a => a.CIR_MasterID).Distinct().ToList();
        var cirMasters = cirIds.Count == 0 ? new() : await _circulars.CIR_Master.AsNoTracking()
            .Where(m => cirIds.Contains(m.CIR_MasterID) && (m.IsDeleted ?? 0) == 0 && m.Status != "Temp" && m.Status != "Draft")
            .Select(m => new { m.CIR_MasterID, m.CircularType })
            .ToListAsync();
        var cirReads = await _circulars.Vw_CIR_VesselAck.AsNoTracking()
            .Where(x => x.CIR_MasterID != null && x.DateRead != null)
            .Select(x => new { CIR_MasterID = x.CIR_MasterID!.Value, x.VesselID, x.UserRank })
            .ToListAsync();

        var manToAck = await _dms.DmManualToAckVsls.AsNoTracking()
            .Where(a => (a.IsActive ?? 1) == 1)
            .Select(a => new { a.DM_ManualID, a.VesselID, a.UserRank })
            .ToListAsync();
        var manIds = manToAck.Select(a => a.DM_ManualID).Distinct().ToList();
        var liveManualIds = manIds.Count == 0 ? new List<long>() : await _dms.DmManuals.AsNoTracking()
            .Where(m => manIds.Contains(m.DM_ManualID) && (m.IsDeleted ?? 0) == 0)
            .Select(m => m.DM_ManualID)
            .ToListAsync();
        var latestVersions = (manIds.Count == 0 ? new() : await _dms.DmManualVersions.AsNoTracking()
                .Where(v => manIds.Contains(v.DM_ManualID) && v.IsActive == 1)
                .Select(v => new { v.DM_ManualID, v.DM_ManualVersionID })
                .ToListAsync())
            .GroupBy(v => v.DM_ManualID)
            .ToDictionary(g => g.Key, g => g.Max(v => v.DM_ManualVersionID));
        var manReads = await _dms.DmManualUserAcks.AsNoTracking()
            .Where(a => a.DateRead != null)
            .Select(a => new { a.DM_ManualID, a.DM_ManualVersionID, a.VesselID, a.UserRank })
            .ToListAsync();

        // ---- Link state and what the ships report (dbo.Sync_Vessel), and what is waiting to go out ----
        result.SyncInstalled = await _sync.IsInstalledAsync();
        var link = new Dictionary<int, LinkInfo>();
        var hasKey = new HashSet<int>();
        var queued = new Dictionary<int, HashSet<(string ItemType, long ItemId)>>();
        if (result.SyncInstalled)
        {
            var links = await _dbContext.SyncVessels.AsNoTracking().ToListAsync();
            foreach (var r in links)
                link[r.VesselID] = new LinkInfo(
                    r.LastContactUtc == null ? null : Utc(r.LastContactUtc.Value),
                    r.LastSuccessUtc == null ? null : Utc(r.LastSuccessUtc.Value),
                    r.LastStatus,
                    r.LastError,
                    r.PendingOnShip,
                    r.CurrentActivity,
                    r.ProgressPercent,
                    r.AppVersion);
            hasKey.UnionWith(await _dbContext.SyncVesselKeys.AsNoTracking().Where(k => k.IsActive).Select(k => k.VesselID).ToListAsync());
            queued = await _sync.GetQueuedItemsAsync(vesselIds);
        }

        var now = DateTime.UtcNow;
        var cirById = cirMasters.ToDictionary(m => m.CIR_MasterID);
        var liveManuals = liveManualIds.Where(latestVersions.ContainsKey).ToHashSet();
        // Lookups so each vessel only touches its own rows.
        var cirByVessel = cirToAck.ToLookup(a => a.VesselID);
        var manByVessel = manToAck.ToLookup(a => a.VesselID);
        var cirReadLookup = cirReads.ToLookup(x => (x.CIR_MasterID, x.VesselID), x => x.UserRank);
        var manReadLookup = manReads.ToLookup(x => (x.DM_ManualID, x.VesselID), x => (x.DM_ManualVersionID, x.UserRank));

        foreach (var v in vessels)
        {
            link.TryGetValue(v.VesselID, out var l);

            var row = new VesselSyncMonitorRow
            {
                VesselID = v.VesselID,
                VslCode = v.VslCode,
                VslName = v.VslName,
                VslType = v.VslType,
                VslEmail = v.VslEmailAddress,
                LastContactUtc = l?.Contact,
                LastSuccessUtc = l?.Success,
                LastStatus = l?.Status,
                LastError = l?.Error,
                PendingOnShip = l?.PendingOnShip ?? 0,
                CurrentActivity = l?.Activity,
                ProgressPercent = l?.Progress,
                AppVersion = l?.AppVersion,
                HasKey = hasKey.Contains(v.VesselID),
                ItemsQueued = queued.TryGetValue(v.VesselID, out var q) ? q.Count : 0,
                Link = l?.Contact == null ? "Not connected"
                     : (now - l.Contact.Value).TotalMinutes <= OnlineMinutes ? "Online" : "Offline"
            };

            // Circulars and alerts (VesselID -1 = all vessels)
            foreach (var g in cirByVessel[v.VesselID].Concat(cirByVessel[-1]).GroupBy(a => a.CIR_MasterID))
            {
                if (!cirById.TryGetValue(g.Key, out var m)) continue;
                row.ItemsTotal++;
                var ranks = RankSet(g.Select(a => a.UserRank));
                if (ranks.Count == 0) continue;
                var read = RankSet(cirReadLookup[(g.Key, v.VesselID)]);
                if (ranks.Any(r => !read.Contains(r)))
                {
                    row.AcksPending++;
                    if (IsAlert(m.CircularType)) row.AlertsPending++;
                }
            }

            // Manuals with a released version (only acknowledgements of the current version count)
            foreach (var g in manByVessel[v.VesselID].Concat(manByVessel[-1]).GroupBy(a => a.DM_ManualID))
            {
                if (!liveManuals.Contains(g.Key)) continue;
                var latest = latestVersions[g.Key];
                row.ItemsTotal++;
                var ranks = RankSet(g.Select(a => a.UserRank));
                if (ranks.Count == 0) continue;
                var read = RankSet(manReadLookup[(g.Key, v.VesselID)]
                                       .Where(x => x.DM_ManualVersionID == latest)
                                       .Select(x => x.UserRank));
                if (ranks.Any(r => !read.Contains(r))) row.AcksPending++;
            }

            // Queued also counts parent manuals sent to complete the tree on board.
            row.ItemsTotal = Math.Max(row.ItemsTotal, row.ItemsQueued);
            result.Vessels.Add(row);
        }

        return result;
    }

    private static bool IsAlert(string? circularType) =>
        circularType != null && circularType.Trim().StartsWith("alert", StringComparison.OrdinalIgnoreCase);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static HashSet<string> RankSet(IEnumerable<string?> ranks) =>
        new(ranks.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!.Trim()), StringComparer.OrdinalIgnoreCase);

    private static VesselSyncItemDto Item(string type, long id, string? number, string? title, string? version, string? category,
                                          DateTime? issuedOn, HashSet<string> requiredRanks, IEnumerable<string?> readRanks, DateTime? lastRead)
    {
        var acknowledged = requiredRanks.Count == 0
            ? 0
            : RankSet(readRanks).Count(r => requiredRanks.Contains(r));
        return new VesselSyncItemDto
        {
            ItemType = type,
            ItemID = id,
            Number = number,
            Title = title,
            Version = version,
            Category = category,
            IssuedOn = issuedOn,
            RanksRequired = requiredRanks.Count,
            RanksAcknowledged = acknowledged,
            LastReadOn = lastRead,
            Status = requiredRanks.Count == 0 ? "Sent"
                   : acknowledged >= requiredRanks.Count ? "Acknowledged"
                   : acknowledged > 0 ? "Partly acknowledged"
                   : "Pending"
        };
    }
}
