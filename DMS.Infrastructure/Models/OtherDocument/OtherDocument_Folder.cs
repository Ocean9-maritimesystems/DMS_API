using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Data.EF.Models.OtherDocument;

[Table("OtherDocument_Folder")]
public sealed class OtherDocument_Folder
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [Column("OtherDocument_FolderID")]
    public long OtherDocument_FolderID { get; set; }
    public string Origin { get; set; } = null!;
    public int VesselID { get; set; }
    public long? ParentID { get; set; }
    public string FolderName { get; set; } = null!;
    public int? SortOrder { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedOn { get; set; }
    public int IsDeleted { get; set; }
}
