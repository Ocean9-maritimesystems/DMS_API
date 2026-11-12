using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Data.EF.Models.Circulars;

[Table("CIR_Settings")]
public partial class CIR_Settings
{
    [Key]
    [Column("CIR_SettingsID")]
    public long CIR_SettingsID { get; set; }

    [StringLength(5)]
    [Unicode(false)]
    public string Origin { get; set; } = null!;

    [Column("VesselID")]
    public int VesselID { get; set; }

    [StringLength(250)]
    [Unicode(false)]
    public string SettingName { get; set; } = null!;

    [StringLength(250)]
    [Unicode(false)]
    public string? SettingValue { get; set; }
}
