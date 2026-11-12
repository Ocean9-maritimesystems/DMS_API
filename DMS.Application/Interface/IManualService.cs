using DMS.Application.DTO.Circulars;
using DMS.Application.DTO.Manuals;

namespace DMS.Application.Interface;

public interface IManualService
{
    public Task<List<Manual_TreeviewDto>> GetTreeViewManualListAsync(string userID);

    /// <summary>Tree with only the manuals matching calledMode; their parents are included but not selectable.</summary>
    public Task<List<Manual_TreeviewDto>> GetTreeViewManualListAsync(string userID, string? calledMode);

    public Task<List<Manual_FilterDto>> GetManualListBasedOnCalledModeAsync(string calledMode, string userID);
    
    public Task<string?> GetManualContent(long dm_ManualID, bool includeSubManuals);

    public Task<List<ManualVersionDetailsDto>> GetManualVersionDetailsAsync(long dm_ManualID);

    public Task<ManualCount_User?> GetUserManualCountAsync(string userID);

    public Task<ManualCount_Dashboard?> GetDashboardManualCountsAsync(string userID);
    
    public Task<CompareVersionDetailsDto> GetCompareVersionDetailsAsync(long dm_ManualID, long manualVersionIDToCompare);

    public Task<List<ManualCategoryDto>> GetManualCategory();

    public Task<List<ManualApprovalFlowListDto>> GetApprovalFlowList();

    public Task<List<ManualApprovalRoleListDto>> GetApprovalDetailsForFlowID(int flowID);

    public Task<List<DmManualAttachment>> GetManualAttachmentListAsync(long dm_ManualID);

    public Task<ManualDetails> GetManualDetailsByVersionAsync(long DM_ManualVersionID);

    public Task<Manual_SimpleDetails> GetManualSimpleDetailsAsync(long dm_ManualID);

    public Task<List<ManualDetails>> GetUserManualListBasedOnCalledModeAsync(string userID, string calledMode);
   
    public Task<List<ManualDetails>> GetUserFavouiteManualListAsync(string userID);

    public Task<AssignManualRightsDto> GetManualRightsUsersAsync();

    public Task<ManualRightsDto> GetDmManualAssignedRightsAsync(long dm_ManualID);

    public Task<List<ManualUserAckList>> GetManualUserAckListAsync(long dm_ManualID);
    
    public Task<List<ManualNotificationDto>> GetManualNotificationListAsync(int limit, string userID);
    
    public Task<List<ManualLogDto>> GetManualLogsAsync(long dm_ManualID);


    public Task UpdateManualRights(ManualRightsDto request);
    
    public Task<CreateManualResultDto> AddDmManualAsync(CreateManualDto request);

    public Task<CreateManualResultDto> UpdateDmManualAsync(EditManualDto request);
    
    public Task<bool> ApproveManualAsync(ManualApproveDto request, bool isToSendBack);

    /// <summary>Submits a draft for release (or to its first approver).</summary>
    public Task<bool> PublishManualAsync(ManualApproveDto request);

    public Task DeleteDmManualAsync(long dm_ManualID, string userID);

    /// <summary>Removes the newest, not yet released, version of a manual.</summary>
    public Task DeleteDmManualVersionAsync(long dm_ManualID, string userID);

    public Task AddToFavouriteAsync(long dm_ManualID, string userID);

    public Task RemoveFromFavouriteAsync(long dm_ManualID, string userID);

    public Task<bool> MarkNotificationReadAsync(long notificationID, string userID);

    public Task<List<ManualVersionHistoryDto>> GetManualVersionHistoryAsync(long dm_ManualID);
}
