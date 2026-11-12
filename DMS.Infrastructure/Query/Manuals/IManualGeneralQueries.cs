using DMS.Application.DTO;
using DMS.Application.DTO.Manuals;
using DMS.Data.EF.Models.Manuals;

namespace DMS.Data.EF.Query.Manuals;

public interface IManualGeneralQueries
{
    public Task<List<Manual_TreeviewEntity>> GetDmManualTreeviewAsync(string userID);

    public Task<List<Manual_TreeviewFilteredEntity>> GetDmManualTreeviewFilteredAsync(string userID, string calledMode);

    public Task<List<ManualVersionDetailsDto>> GetDmManualVersionList(long dm_ManualID);

    public Task<List<ManualDetails>> GetUserActionManualList(string userID, string calledMode);

    public Task<List<VwManualNotification>> GetNotificationList(string userID, int limit);

    public Task<ManualCount_User> GetUserManualCount(string userID);

    public Task AddToLog(long dm_ManualID, long dm_ManualVersionID, string activity, string remarks);

    public Task AddNotification(long dm_ManualID, long dm_ManualVersionID, string userID, string notificationType, string notificationTitle, string description);

    public Task<string?> GetDmManualContentAsync(long dm_ManualID, bool includeSubManuals);

    public Task<List<VesselListDto>> GetVesselList();

    public Task<List<VslRankListDto>> GetVslRankList();

    public Task<List<HQUserListDto>> GetHQUserList();

    public Task<List<ManualApprovalRoleListDto>> GetManualApprovalFlowDetails(int flowID);

    public Task<long> GetNextApprovalStatusID(int approval_FlowID, long current_DM_StatusID);

    public Task<long> GetSendBackApprovalStatusID(int approval_FlowID, long current_DM_StatusID);

}
