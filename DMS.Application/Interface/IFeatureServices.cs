using DMS.Application.DTO.Features;

namespace DMS.Application.Interface
{
    public interface IAckDashboardService
    {
        Task<List<AckVesselSummary>> GetVesselSummaryAsync();
        Task<List<AckPendingRow>> GetVesselPendingAsync(int? vesselId, string? itemType, string? rank);
        Task<List<AckPendingRow>> GetOfficePendingAsync(string? userId);
        Task<FeatureSettingsDto> GetSettingsAsync();
        Task SaveSettingsAsync(FeatureSettingsDto settings);
    }

    public interface ISuggestionService
    {
        Task<List<SuggestionListItem>> GetListAsync(string? status, int? vesselId, string? submittedBy);
        Task<SuggestionDetail?> GetAsync(long id);
        Task<long> CreateAsync(NewSuggestion request, OfficeUser user);
        Task<string?> ChangeStatusAsync(long id, SuggestionStatusChange request, OfficeUser user);
        Task AddCommentAsync(long id, string comment, OfficeUser user);
        Task<FileContent?> GetFileAsync(long blobId);
    }

    public interface ISearchService
    {
        Task<List<SearchHit>> SearchAsync(string query, string? types, OfficeUser user);
    }

    public interface IInboxService
    {
        Task<List<InboxItem>> GetListAsync(OfficeUser user, bool unreadOnly, string? itemType, string? search, int take);
        Task<int> GetUnreadCountAsync(OfficeUser user);
        Task<bool> MarkReadAsync(long inboxId, bool read, OfficeUser user);
        Task<int> MarkAllReadAsync(OfficeUser user);
    }

    public interface IReportService
    {
        /// <summary>The rows of one report (see ReportService for the keys).</summary>
        Task<List<Dictionary<string, object?>>> RunAsync(string key, ReportFilter filter);
    }
}
