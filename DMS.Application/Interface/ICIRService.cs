using DMS.Application.DTO.Circulars;
using DMS.Application.DTO.Features;
using DMS.Data.EF.Models.Circulars;

namespace DMS.Application.Interface;

public interface ICIRService
{
    public Task<List<Circulars_TreeviewDto>> GetTreeViewCircularsListAsync(string userID, string circularType);
   
    public Task<List<CIRMasterList>> GetCIRMasterListAsync(string userID, long categoryId, string circularType);
  
    public Task<List<CircularCategoryDto>> GetCategoriesAsync(bool isToIncludeAll, string? circularType = null);

    public Task<List<CircularsChart>> GetCIR_CategoryChartDataAsync(string circularType);

    public Task<List<CIRRecentList>> GetRecentCircularsAsync(string circularType, int limit = 10);
    
    public Task<CircularCount_Dashboard> GetDashboardCircularCountsAsync();
    
    public Task<List<CircularsNotificationDto>> GetCIRNotificationListAsync(int limit, string userID);

    public Task<List<CircularUserAckDto>> GetVesselAckListAsync(long cir_MasterID);

    public Task<GetNextCIRNumberResponseDto> GetNextCIRNumbersByCategoryAsync(long categoryId, long cir_MasterID);

    public Task<EditCircularDto> GetCIRDetailsAsync(long cirMasterId);

    public Task<List<CIR_LogDto>> GetCircularLogsAsync(long cirMasterId);

    public Task RemoveTempCIRAsync(long cir_MasterID);

    public Task<long> InsertCIR_MasterRecord(string circularType);

    public Task<long> CreateCircularAsync(CreateCircularDto createDto);

    public Task AddToFavouriteAsync(long cir_MasterID, string circularType, string userID);

    public Task RemoveFromFavouriteAsync(long cir_MasterID, string userID);

    public Task<long> UpdateCircularAsync(EditCircularDto editDto);

    public Task<bool> MarkNotificationReadAsync(long notificationID, string userID);

    public Task<CircularExtras?> GetCircularesExtraInfoAsync(long circularId);
    public Task<string?> SaveCircularesExtraInfoAsync(long circularId, SaveCircularExtras request);
    public Task<string?> AddManualLinkAsync(long circularId, AddManualLink request, OfficeUser user);
    public Task RemoveManualLinkAsync(long linkId);
    public Task<string?> CancelCircularAsync(long circularId, CancelCircular request, OfficeUser user);

    /// <summary>Draft -> Active: sends the circular to the vessels. Returns why it could not be released, or null.</summary>
    public Task<string?> ReleaseCircularAsync(long circularId, OfficeUser user);

    /// <summary>Saves a draft (like EditCircular), then releases it. Returns why it could not, or null.</summary>
    public Task<string?> SaveAndReleaseCircularAsync(EditCircularDto editDto, OfficeUser user);
    public Task<List<CircularManualLink>> GetForManualAsync(long manualId);
    public Task<int> CloseLinksForManualAsync(long manualId);
    public Task<List<CircularCategorySetting>> GetCategoriesAsync();
    public Task SaveCategoryAsync(long categoryId, bool requiresValidUntil);
    public Task<ManualExtras?> GetManualAsync(long manualId);
    public Task SaveManualAsync(long manualId, int? ackDueDays);
}
