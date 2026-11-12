namespace DMS.API.Models.Reports;

public class ReportRequest
{
    public int? VesselId { get; set; }
    public string? ItemType { get; set; }
    public string? Status { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public bool OverdueOnly { get; set; }
    public int? Days { get; set; }
    public bool TopLevelOnly { get; set; }
}
