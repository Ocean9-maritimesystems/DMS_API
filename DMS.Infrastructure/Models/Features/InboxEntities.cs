using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Models.Features;

[Table("DMS_Inbox")]
public class DmsInbox
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] 
    public long DMS_InboxID { get; set; }

    [StringLength(3), Unicode(false)] 
    public string Origin { get; set; } = "HQ";

    public int VesselID { get; set; }

    [StringLength(20), Unicode(false)] 
    public string ItemType { get; set; } = "";

    public long ItemID { get; set; }

    public long? VersionID { get; set; }

    [StringLength(20), Unicode(false)] 
    public string Action { get; set; } = "";

    [StringLength(500)]
    public string? Title { get; set; }

    public int? Priority { get; set; }

    [StringLength(50), Unicode(false)] 
    public string? TargetUserID { get; set; }

    [StringLength(50), Unicode(false)]
    public string? TargetRank { get; set; }

    [Column(TypeName = "datetime")] 
    public DateTime CreatedOn { get; set; }

    public int IsActive { get; set; } = 1;
}

[Table("DMS_InboxRead")]
public class DmsInboxRead
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] 
    public long DMS_InboxReadID { get; set; }

    [StringLength(3), Unicode(false)]
    public string Origin { get; set; } = "HQ";

    public int VesselID { get; set; }

    public long DMS_InboxID { get; set; }

    [StringLength(50), Unicode(false)] 
    public string UserID { get; set; } = "";

    [Column(TypeName = "datetime")]
    public DateTime ReadOn { get; set; }
}

[Table("Notification_Read")]
[PrimaryKey(nameof(Module), nameof(UserID), nameof(NotificationID))]
public class NotificationRead
{
    [StringLength(3), Unicode(false)] 
    public string Module { get; set; } = "";

    public long NotificationID { get; set; }

    [StringLength(50), Unicode(false)] 
    public string UserID { get; set; } = "";

    [Column(TypeName = "datetime2(0)")] 
    public DateTime ReadOn { get; set; }
}