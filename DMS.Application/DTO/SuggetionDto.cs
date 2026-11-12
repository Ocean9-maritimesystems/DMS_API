using DMS.Application.DTO.Features;

namespace DMS.Application.DTO.Features;

public class SuggestionListItem
{
    public long SUG_MasterID { get; set; }
    public int VesselID { get; set; }
    public string? VesselName { get; set; }
    public string? SuggestionNo { get; set; }
    public string Title { get; set; } = "";
    public string? ItemType { get; set; }
    public long? ItemID { get; set; }
    public string? ItemTitle { get; set; }
    public string Priority { get; set; } = "";
    public string Status { get; set; } = "";
    public string SubmittedBy { get; set; } = "";
    public string? SubmittedByName { get; set; }
    public string? SubmittedRank { get; set; }
    public DateTime SubmittedOn { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

public class SuggestionDetail : SuggestionListItem
{
    public string? Description { get; set; }
    public string? QuotedText { get; set; }
    public string? ImplementedItemType { get; set; }
    public long? ImplementedItemID { get; set; }
    public List<SuggestionLog> Log { get; set; } = new();
    public List<FileItem> Files { get; set; } = new();
}

public class SuggestionLog
{
    public long SUG_LogID { get; set; }
    public string? Status { get; set; }
    public string? Comment { get; set; }
    public string? UserID { get; set; }
    public string? UserName { get; set; }
    public DateTime LogDate { get; set; }
}

public sealed record NewSuggestion(string Title, string? Description, string? ItemType, long? ItemId, string? QuotedText,
    string? Priority, List<UploadedFile> Files);

public sealed record SuggestionStatusChange(string Status, string? Comment, string? ImplementedItemType, long? ImplementedItemId);

public class FileItem
{
    public long Id { get; set; }
    public long? FolderID { get; set; }
    public string FileName { get; set; } = "";
    public string? FileType { get; set; }
    public int BlobSize { get; set; }
    public int Version { get; set; } = 1;
    public string? UploadedBy { get; set; }
    public string? UploadedByName { get; set; }
    public DateTime? UploadedOn { get; set; }
}

public sealed record UploadedFile(string FileName, string? ContentType, byte[] Bytes);
public sealed record FileContent(byte[] Bytes, string ContentType, string FileName);
