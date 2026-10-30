using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Data.EF.Models.Circulars;

[Table("CIR_ToAck_HQ")]
public partial class CIR_ToAck_HQ
{
    [Key]
    [Column("CIR_ToAck_HQID")]
    public long CIR_ToAck_HQID { get; set; }

    [StringLength(3)]
    [Unicode(false)]
    public string Origin { get; set; } = null!;

    [Column("VesselID")]
    public int VesselID { get; set; }

    [Column("CIR_MasterID")]
    public long CIR_MasterID { get; set; }

    [Column("HQ_UsersID")]
    public long? HqUsersId { get; set; }

    [StringLength(50)]
    [Unicode(false)]
    public string? Role { get; set; }

    public int? IsActive { get; set; }
}
