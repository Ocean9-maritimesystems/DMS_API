using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Application.DTO.OtherDocument;

[Table("OtherDocument_Type")]
public partial class OtherDocument_Type
{
    [Key]
    [Column("OtherDocument_TypeID")]
    public long OtherDocument_TypeID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = null!;

    [Column("VesselID")]
    public int VesselID { get; set; }

    [StringLength(25)]
    public string DocumentCode { get; set; } = null!;

    [StringLength(150)]
    public string? DocumentType { get; set; }

    public int? IsActive { get; set; }

    public int SortOrder { get; set; }
}