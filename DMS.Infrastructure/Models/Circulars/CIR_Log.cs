using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Data.EF.Models.Circulars;

[Table("CIR_Log")]
public partial class CIR_Log
{
    [Key]
    [Column("CIR_LogID")]
    public long CIR_LogID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = null!;

    [Column("VesselID")]
    public int VesselID { get; set; }

    [Column("CIR_MasterID")]
    public long? CIR_MasterID { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? LogDate { get; set; }

    [StringLength(150)]
    public string? Activity { get; set; }

    [StringLength(3000)]
    public string? Remarks { get; set; }

    [StringLength(150)]
    public string? UserId { get; set; }

    [StringLength(150)]
    [Unicode(false)]
    public string? UserName { get; set; }

    // The office table has no Fleet_CrewId column (only the ship keeps it), so EF must not write it.
    [NotMapped]
    public long? Fleet_CrewId { get; set; }

    [StringLength(50)]
    [Unicode(false)]
    public string? UserRank { get; set; }
}
