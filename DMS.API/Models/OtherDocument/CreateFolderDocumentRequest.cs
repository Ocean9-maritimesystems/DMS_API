namespace DMS.API.Models.OtherDocument;

public class CreateFolderDocumentRequest
{
    public long TypeId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? Reference { get; set; }
    public string? Comments { get; set; }
    public int? VesselId { get; set; }
    public List<IFormFile>? Files { get; set; }
    public bool SendToVessel { get; set; }
}
