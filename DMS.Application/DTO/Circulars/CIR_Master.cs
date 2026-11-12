using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Application.DTO.Circulars;

[Table("CIR_Master")]
public partial class CIR_Master
{
    [Key]
    [Column("CIR_MasterID")]
    public long CIR_MasterID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = null!;

    [Column("VesselID")]
    public int VesselID { get; set; }

    [StringLength(15)]
    public string CircularType { get; set; } = null!;

    [Column("CIR_CategoryID")]
    public long? CIR_CategoryID { get; set; }

    [StringLength(50)]
    public string? CIR_Number { get; set; }

    [StringLength(150)]
    public string? Reference { get; set; }

    [StringLength(500)]
    [Unicode(false)]
    public string? Title { get; set; }

    [StringLength(50)]
    [Unicode(false)]
    public string? Status { get; set; }

    public int? Priority { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? DateIssued { get; set; }

    [StringLength(500)]
    public string? Remarks { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? ReleasedDate { get; set; }

    [StringLength(50)]
    public string? CreatedBy { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? CreatedDate { get; set; }

    [StringLength(50)]
    public string? ModifiedBy { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? ModifiedDate { get; set; }

    public int? IsDeleted { get; set; }

    /// <summary>Days the vessels have to acknowledge (012_NewFeatures.sql).</summary>
    public int? AckDueDays { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? ValidUntil { get; set; }

    /// <summary>The circular that replaced this one when it was cancelled.</summary>
    public long? SupersededByID { get; set; }
}
