using DMS.Data.EF.Context;
using DMS.Application.DTO.Circulars;
using DMS.Application.Interface;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Circulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace DMS.Data.EF.Query.Circulars;

public class CIRGeneralQueries : ICIRGeneralQueries
{
    private readonly CircularsContext _dbContext;
    private readonly GeneralContext _generalContext;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUser _currentUser;

    public CIRGeneralQueries(CircularsContext dbContext, GeneralContext generalContext, IConfiguration config, ICurrentUser currentUser)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(_dbContext));
        _generalContext = generalContext ?? throw new ArgumentNullException(nameof(_generalContext));
        _configuration = config ?? throw new ArgumentNullException("config");
        _currentUser = currentUser;
    }

    public async Task<List<CIR_TreeviewEntity>> GetCIRTreeviewAsync(string userID, string circularType)
    {
        var result = await _dbContext
                            .CIR_Treeview
                            .FromSqlInterpolated($"EXEC GetCIR_TreeViewList @UserID = {userID}, @CircularType = {circularType}")
                            .AsNoTracking()
                            .ToListAsync();

        return result ?? new List<CIR_TreeviewEntity>();
    }

    public async Task<List<CIRMasterList>> GetCIRListAsync(string userID, long cir_CategoryID, string circularType)
    {
        var result = await _dbContext
                            .CIRMasterList
                            .FromSqlInterpolated($"EXEC GetCIRMasterList @UserID = {userID}, @CIR_CategoryID = {cir_CategoryID}, @CircularType = {circularType}")
                            .AsNoTracking()
                            .ToListAsync();

        return result ?? new List<CIRMasterList>();
    }

    public async Task<List<CircularsChart>> GetCIRCategoryBasedChartDataAsync(string circularType)
    {
        var result = await _dbContext
                            .Set<CircularsChart>()
                            .FromSqlInterpolated($"EXEC GetCIR_CategoryChartData @CircularType = {circularType}")
                            .AsNoTracking()
                            .ToListAsync();

        return result ?? new List<CircularsChart>();
    }

    public async Task<List<CIRRecentList>> GetRecentCircularsAsync(string circularType, int limit)
    {
        var result = await _dbContext
                            .Set<CIRRecentList>()
                            .FromSqlInterpolated($"EXEC GetCIR_RecentList @CircularType = {circularType}, @Limit = {limit}")
                            .AsNoTracking()
                            .ToListAsync();

        return result ?? new List<CIRRecentList>();
    }

    public async Task<CircularCount_Dashboard> GetCIRDashboardCountsAsync()
    {
        var result = await _dbContext
                            .Set<CircularCount_Dashboard>()
                            .FromSqlInterpolated($"EXEC GetCIR_DashboardCount")
                            .AsNoTracking()
                            .ToListAsync();
        var dashboard = new CircularCount_Dashboard();

        if (result != null && result.Count > 0)
        {
            var r = result[0];
            dashboard.TotalCirculars = r.TotalCirculars;
            dashboard.TotalAlerts = r.TotalAlerts;
        }

        return dashboard;
    }

    /// <summary>The newest circular/alert notifications of the last 30 days meant for everyone ('') or for this user.</summary>
    public async Task<List<VwCIRNotification>> GetNotificationList(string userID, int limit)
    {
        var since = DateTime.Now.AddDays(-30);
        return await _dbContext
                        .VwCIRNotification
                        .Where(x => x.CreatedDate >= since && (x.UserID == null || x.UserID == "" || x.UserID == userID))
                        .OrderByDescending(x => x.CreatedDate)
                        .Take(NotificationReads.ClampLimit(limit))
                        .AsNoTracking()
                        .ToListAsync();
    }

    public async Task<List<VwCIRNotification>> GetNotificationList()
    {
        var result = await _dbContext
                            .VwCIRNotification
                            .Where(x => x.CreatedDate >= DateTime.Now.AddDays(-30))
                            .OrderByDescending(x => x.CreatedDate)
                            .AsNoTracking()
                            .ToListAsync();

        return result ?? new List<VwCIRNotification>();
    }

    public async Task<List<CircularUserAckDto>> GetVesselAckListAsync(long cir_MasterID)
    {
        var vessels = await _generalContext.VVessels.AsNoTracking().ToListAsync();
        var ackVsl = await _dbContext.CIR_ToAck_Vsl
                            .Where(a => a.CIR_MasterID == cir_MasterID)
                            .AsNoTracking()
                            .ToListAsync();
        var userAcks = await _dbContext.Vw_CIR_VesselAck
                           .Where(x => x.CIR_MasterID == cir_MasterID)
                           .AsNoTracking()
                           .ToListAsync();

        var userAckByVsl = userAcks
                          .GroupBy(u => (VesselId: Convert.ToInt64(u.VesselID), UserRank: u.UserRank))
                          .ToDictionary(g => g.Key, g => g.First());


        var result = new List<CircularUserAckDto>(vessels.Count);
        foreach (var vsl in vessels)
        {
            var vslId = Convert.ToInt64(vsl.VesselID);

            // Only the ranks required for this vessel. VesselID -1 means "all vessels".
            // (Before, every vessel was listed with every rank, so a circular sent to one
            // vessel showed all vessels in Acknowledgment Status.)
            var ackForVessel = ackVsl.Where(a => a.VesselID == vslId || a.VesselID == -1)
                                     .GroupBy(a => a.UserRank)
                                     .Select(g => g.First());

            foreach (var ack in ackForVessel)
            {
                userAckByVsl.TryGetValue((vslId, ack.UserRank), out var ua);

                var dateRead = ua?.DateRead;
                var userName = ua?.UserName;
                var userRank = ack.UserRank;
                var comments = ua?.Comments;

                var dto = new CircularUserAckDto
                {
                    VesselID = vsl.VesselID,
                    VslCode = vsl.VslCode,
                    VslName = vsl.VslName,
                    DateRead = dateRead.HasValue ? dateRead.Value.ToString("dd-MMM-yyyy") : " ",
                    Status = (userName != null || dateRead.HasValue) ? "Acknowledged" :  "Pending",
                    AcknowledgedBy = (userName != null)
                                        ? $"{(userName ?? string.Empty)} ({(userRank ?? string.Empty)})"
                                        : $"Pending ({(userRank ?? string.Empty)})",
                    Comments = comments
                };

                result.Add(dto);
            }
        }

        return result;
    }

    public async Task AddToLog(long cir_MasterID, string activity, string remarks)
    {
        var cir_LogID = await _dbContext.NextIdAsync<CIR_Log>();

        var manualLog = new CIR_Log
        {
            CIR_LogID = cir_LogID,
            Origin = "HQ",
            VesselID = -1,
            CIR_MasterID = cir_MasterID,
            Activity = activity,
            Remarks = remarks,
            UserName = _currentUser.UserId,
            UserRank = "",
            LogDate = DateTime.Now,
        };

        _dbContext.CIR_Log.Add(manualLog);
        await _dbContext.SaveChangesAsync();
    }

    public async Task AddNotification(long cir_MasterID, string userID, string notificationType, string notificationTitle, string description)
    {
        var nextCIR_NotificationID = await _dbContext.NextIdAsync<CIR_Notification>();

        var notification = new CIR_Notification();
        notification.CIR_NotificationID = nextCIR_NotificationID;
        notification.Origin = "HQ";
        notification.VesselID = -1;
        notification.Description = description;
        notification.NotificationType = notificationType;
        notification.NotificationTitle = notificationTitle;
        notification.CIR_MasterID = cir_MasterID;
        notification.UserID = userID;
        notification.IsRead = 0;
        notification.CreatedDate = DateTime.Now;
        await _dbContext.CIR_Notification.AddAsync(notification);

        await _dbContext.SaveChangesAsync();
    }
}
