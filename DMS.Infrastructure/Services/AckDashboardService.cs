using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using Microsoft.EntityFrameworkCore;

namespace DMS.Infrastructure.Services;

public sealed class AckDashboardService : IAckDashboardService
{
    private readonly SqlDb _db;
    private readonly GeneralContext _dbContext;

    public AckDashboardService(SqlDb db, GeneralContext dbContext)
    {
        _db = db;
        _dbContext = dbContext;
    }

    private Task<List<AckPendingRow>> ReadPendingAsync(int? vesselId, string? userId, bool office)
        => _dbContext.Set<AckPendingRow>()
            .FromSqlInterpolated($"EXEC dbo.Ack_GetPending @VesselID = {vesselId}, @UserID = {userId}, @Office = {office}")
            .AsNoTracking()
            .ToListAsync();

    public async Task<List<AckPendingRow>> GetVesselPendingAsync(int? vesselId, string? itemType, string? rank)
        => (await ReadPendingAsync(vesselId, null, false))
            .Where(r => string.IsNullOrEmpty(itemType) || string.Equals(r.ItemType, itemType, StringComparison.OrdinalIgnoreCase))
            .Where(r => string.IsNullOrEmpty(rank) || string.Equals(r.Rank, rank, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.DueOn ?? DateTime.MaxValue).ThenBy(r => r.VesselName).ThenBy(r => r.Rank).ThenBy(r => r.UserName)
            .ToList();

    public async Task<List<AckVesselSummary>> GetVesselSummaryAsync()
    {
        var pending = await ReadPendingAsync(null, null, false);
        var vessels = await _dbContext.VVessels.AsNoTracking()
            .Where(v => v.VesselID > 0 && (v.IsRetired == null || v.IsRetired == 0))
            .OrderBy(v => v.VslName)
            .Select(v => new AckVesselSummary { VesselID = v.VesselID, VesselName = v.VslName })
            .ToListAsync();

        var sync = await _dbContext.SyncVessels.AsNoTracking()
            .Select(s => new { s.VesselID, s.LastSuccessUtc })
            .ToDictionaryAsync(s => s.VesselID, s => s.LastSuccessUtc);
        foreach (var vessel in vessels)
            vessel.LastSyncUtc = sync.TryGetValue(vessel.VesselID, out var lastSync) ? lastSync : null;

        foreach (var vessel in vessels)
        {
            var mine = pending.Where(p => p.VesselID == vessel.VesselID).ToList();
            vessel.ManualsPending = mine.Count(p => p.ItemType == "Manual");
            vessel.ManualsOverdue = mine.Count(p => p.ItemType == "Manual" && p.Overdue);
            vessel.CircularsPending = mine.Count(p => p.ItemType == "Circular");
            vessel.CircularsOverdue = mine.Count(p => p.ItemType == "Circular" && p.Overdue);
            vessel.AlertsPending = mine.Count(p => p.ItemType == "Alert");
            vessel.AlertsOverdue = mine.Count(p => p.ItemType == "Alert" && p.Overdue);
        }
        return vessels;
    }

    public async Task<List<AckPendingRow>> GetOfficePendingAsync(string? userId)
        => (await ReadPendingAsync(null, userId, true))
            .OrderBy(r => r.DueOn ?? DateTime.MaxValue).ThenBy(r => r.UserName).ToList();

    public async Task<FeatureSettingsDto> GetSettingsAsync()
    {
        static int Int(string? value, int fallback) => int.TryParse(value, out var number) ? number : fallback;
        return new FeatureSettingsDto
        {
            AllowedRolesToViewAllRankPendingItems = await _db.SettingAsync("DM_Settings", "AllowedRolesToViewAllRankPendingItems"),
            DefaultAckDueDaysManual = Int(await _db.SettingAsync("DM_Settings", "DefaultAckDueDays"), 7),
            DefaultAckDueDaysCircular = Int(await _db.SettingAsync("CIR_Settings", "DefaultAckDueDays_Circular"), 7),
            DefaultAckDueDaysAlert = Int(await _db.SettingAsync("CIR_Settings", "DefaultAckDueDays_Alert"), 1),
            FolderMaxFileSizeMB = Int(await _db.SettingAsync("DM_Settings", "FLD_MaxFileSizeMB"), 25),
            FolderMaxFiles = Int(await _db.SettingAsync("DM_Settings", "FLD_MaxFiles"), 1000)
        };
    }

    public async Task SaveSettingsAsync(FeatureSettingsDto settings)
    {
        await _db.SetSettingAsync("DM_Settings", "AllowedRolesToViewAllRankPendingItems", settings.AllowedRolesToViewAllRankPendingItems?.Trim());
        await _db.SetSettingAsync("DM_Settings", "DefaultAckDueDays", Math.Max(0, settings.DefaultAckDueDaysManual).ToString());
        await _db.SetSettingAsync("CIR_Settings", "DefaultAckDueDays_Circular", Math.Max(0, settings.DefaultAckDueDaysCircular).ToString());
        await _db.SetSettingAsync("CIR_Settings", "DefaultAckDueDays_Alert", Math.Max(0, settings.DefaultAckDueDaysAlert).ToString());
        await _db.SetSettingAsync("DM_Settings", "FLD_MaxFileSizeMB", Math.Max(1, settings.FolderMaxFileSizeMB).ToString());
        await _db.SetSettingAsync("DM_Settings", "FLD_MaxFiles", Math.Max(1, settings.FolderMaxFiles).ToString());
    }
}
