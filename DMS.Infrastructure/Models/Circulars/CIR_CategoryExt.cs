using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Data.EF.Models.Circulars;

[Table("CIR_CategoryExt")]
public partial class CIR_CategoryExt
{
    [Key]
    [Column("CIR_CategoryExtID")]
    public long CIR_CategoryExtID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = null!;

    [Column("VesselID")]
    public int VesselID { get; set; }

    [Column("CIR_CategoryID")]
    public long? CIR_CategoryID { get; set; }

    [StringLength(250)]
    public string? AllowedUsersToAck { get; set; }
}
