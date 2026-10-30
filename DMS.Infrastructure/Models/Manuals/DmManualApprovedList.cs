using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Data.EF.Models.Manuals;

[Table("DM_Manual_ApprovedList")]
public partial class DmManualApprovedList
{
    [Key]
    [Column("DM_Manual_ApprovedListID")]
    public long DmManualApprovedListId { get; set; }

    [StringLength(3)]
    [Unicode(false)]
    public string Origin { get; set; } = null!;

    [Column("VesselID")]
    public int VesselID { get; set; }

    [Column("DM_ManualID")]
    public long DM_ManualID { get; set; }

    [Column("UserID")]
    [StringLength(50)]
    [Unicode(false)]
    public string UserId { get; set; } = null!;

    [StringLength(50)]
    [Unicode(false)]
    public string UserRole { get; set; } = null!;

    [StringLength(250)]
    [Unicode(false)]
    public string UserName { get; set; } = null!;

    [StringLength(50)]
    [Unicode(false)]
    public string? HQDept { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? ApprovedDate { get; set; }

    [StringLength(250)]
    [Unicode(false)]
    public string? Comments { get; set; }

    /// <summary>The manual version the approval was given for (015_BackendFixes.sql).</summary>
    [Column("DM_ManualVersionID")]
    public long? DM_ManualVersionID { get; set; }

    /// <summary>1 = the version was sent back or deleted; the approval no longer counts.</summary>
    public int? IsVoid { get; set; }
}
