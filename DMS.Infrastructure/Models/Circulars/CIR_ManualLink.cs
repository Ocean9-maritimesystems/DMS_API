using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Data.EF.Models.Circulars;

[Table("CIR_ManualLink")]
public class CirManualLink
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] public long CIR_ManualLinkID { get; set; }
    [StringLength(3), Unicode(false)] public string Origin { get; set; } = "HQ";
    public int VesselID { get; set; }
    public long CIR_MasterID { get; set; }
    public long DM_ManualID { get; set; }
    public long? DM_ManualVersionID { get; set; }
    [StringLength(20), Unicode(false)] public string LinkType { get; set; } = "";
    [StringLength(500)] public string? Remarks { get; set; }
    public int IsActive { get; set; } = 1;
    [Column(TypeName = "datetime")] public DateTime? ClosedOn { get; set; }
    [StringLength(50), Unicode(false)] public string? CreatedBy { get; set; }
    [Column(TypeName = "datetime")] public DateTime? CreatedOn { get; set; }
}