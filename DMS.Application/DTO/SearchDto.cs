namespace DMS.Application.DTO.Features;

public class SearchHit
{
    public string ItemType { get; set; } = "";
    public long ItemID { get; set; }
    public string? Number { get; set; }
    public string? Title { get; set; }
    public string? Snippet { get; set; }
    public int? VesselID { get; set; }
    public DateTime? Date { get; set; }
}