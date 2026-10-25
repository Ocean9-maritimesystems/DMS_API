using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Data.EF.Models.OtherDocument;

[Table("OtherDocumentMaster")]
public partial class OtherDocumentMaster
{
    [Key]
    [Column("OtherDocumentMasterID")]
    public long OtherDocumentMasterID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = null!;

    [Column("VesselID")]
    public int VesselID { get; set; }

    public long? OtherDocument_FolderID { get; set; }

    /// <summary>
    /// 1 = added at the office and kept there: never sent to a vessel, even though VesselID is the vessel it is about
    /// (Database/017_OtherDocument_IsForOfficeOnly.sql). 0 = a normal document.
    /// </summary>
    public int IsForOfficeOnly { get; set; }

    [Column("OtherDocument_TypeID")]
    public long? OtherDocument_TypeID { get; set; }

    [StringLength(50)]
    public string DocumentNumber { get; set; } = null!;

    [StringLength(500)]
    public string DocumentTitle { get; set; } = null!;

    [StringLength(50)]
    public string? Status { get; set; }

    [StringLength(150)]
    public string? Reference { get; set; }

    [StringLength(500)]
    public string? Comments { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? UploadedOn { get; set; }

    [StringLength(150)]
    public string? UploadedBy { get; set; }

    public int? IsDeleted { get; set; }

    [NotMapped]
    public string? CreatedBy { get; set; }

    [NotMapped]
    public DateTime? CreatedDate { get; set; }

    [StringLength(50)]
    public string? ModifiedBy { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? ModifiedDate { get; set; }


}