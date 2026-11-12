namespace DMS.Application.DTO.Features;

public class CircularManualLink
{
    public long CIR_ManualLinkID { get; set; }
    public long CIR_MasterID { get; set; }
    public string? CircularNumber { get; set; }
    public string? CircularTitle { get; set; }
    public string? CircularType { get; set; }
    public string? CircularStatus { get; set; }
    public long DM_ManualID { get; set; }
    public string? ManualNo { get; set; }
    public string? ManualName { get; set; }
    public long? DM_ManualVersionID { get; set; }
    public string LinkType { get; set; } = "";
    public string? Remarks { get; set; }
    public bool IsActive { get; set; }
    public DateTime? ClosedOn { get; set; }
    public DateTime? CreatedOn { get; set; }
}

public class CircularExtras
{
    public long CIR_MasterID { get; set; }
    public string? Status { get; set; }
    public int? AckDueDays { get; set; }
    public DateTime? ValidUntil { get; set; }
    public long? SupersededByID { get; set; }
    public string? SupersededByNumber { get; set; }
    public bool RequiresValidUntil { get; set; }
    public List<CircularManualLink> Links { get; set; } = new();
}

public sealed record SaveCircularExtras(int? AckDueDays, DateTime? ValidUntil);
public sealed record AddManualLink(long ManualId, long? ManualVersionId, string LinkType, string? Remarks);
public sealed record CancelCircular(long? SupersededById, string? Reason);

public class CircularCategorySetting
{
    public long CIR_CategoryID { get; set; }
    public string? CircularType { get; set; }
    public string? CategoryCode { get; set; }
    public string? Category { get; set; }
    public bool RequiresValidUntil { get; set; }
}

public class ManualExtras
{
    public long DM_ManualID { get; set; }
    public int? AckDueDays { get; set; }
}