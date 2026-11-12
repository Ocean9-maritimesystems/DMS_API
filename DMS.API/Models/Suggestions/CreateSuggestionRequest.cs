using Microsoft.AspNetCore.Http;

namespace DMS.API.Models.Suggestions;

public class CreateSuggestionRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ItemType { get; set; }
    public long? ItemId { get; set; }
    public string? QuotedText { get; set; }
    public string? Priority { get; set; }
    public List<IFormFile>? Files { get; set; }
}
