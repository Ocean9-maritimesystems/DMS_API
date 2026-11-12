namespace DMS.Application.DTO.Features;

/// <summary>One line of the new-items inbox (dbo.DMS_Inbox) for the logged-in office user.</summary>
public sealed class InboxItem
{
    public long DMS_InboxID { get; set; }
    public int VesselID { get; set; }
    public string ItemType { get; set; } = "";
    public long ItemID { get; set; }
    public long? VersionID { get; set; }
    public string Action { get; set; } = "";
    public string? Title { get; set; }
    public int? Priority { get; set; }
    public DateTime CreatedOn { get; set; }
    /// <summary>The user has a DMS_InboxRead row for this line.</summary>
    public bool IsRead { get; set; }
}
