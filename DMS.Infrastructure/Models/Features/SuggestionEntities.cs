using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Models.Features;


[Table("SUG_Master")]
public class SugMaster
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] 
    public long SUG_MasterID { get; set; }

    [StringLength(3), Unicode(false)]
    public string Origin { get; set; } = "HQ";

    public int VesselID { get; set; }

    [StringLength(30), Unicode(false)] 
    public string? SuggestionNo { get; set; }

    [StringLength(250)] 
    public string Title { get; set; } = "";

    public string? Description { get; set; }

    [StringLength(20), Unicode(false)] 
    public string? ItemType { get; set; }

    public long? ItemID { get; set; }

    [StringLength(1000)]
    public string? QuotedText { get; set; }

    [StringLength(10), Unicode(false)] 
    public string Priority { get; set; } = "Medium";

    [StringLength(20), Unicode(false)] 
    public string Status { get; set; } = "Submitted";

    [StringLength(50), Unicode(false)] 
    public string SubmittedBy { get; set; } = "";

    [StringLength(250)] 
    public string? SubmittedByName { get; set; }

    [StringLength(50), Unicode(false)]
    public string? SubmittedRank { get; set; }

    [Column(TypeName = "datetime")] 
    public DateTime SubmittedOn { get; set; }

    [StringLength(50), Unicode(false)] 
    public string? AssignedTo { get; set; }

    [StringLength(20), Unicode(false)] 
    public string? ImplementedItemType { get; set; }

    public long? ImplementedItemID { get; set; }
    [Column(TypeName = "datetime")] 

    public DateTime? ModifiedOn { get; set; }

    public int IsDeleted { get; set; }
}

[Table("SUG_Log")]
public class SugLog
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] 
    public long SUG_LogID { get; set; }

    [StringLength(3), Unicode(false)] 
    public string Origin { get; set; } = "HQ";
    public int VesselID { get; set; }
    public long SUG_MasterID { get; set; }

    [StringLength(20), Unicode(false)] 
    public string? Status { get; set; }

    [StringLength(2000)] 
    public string? Comment { get; set; }

    [StringLength(50), Unicode(false)] 
    public string? UserID { get; set; }

    [StringLength(250)] 
    public string? UserName { get; set; }

    [Column(TypeName = "datetime")] 
    public DateTime LogDate { get; set; }
}

[Table("SUG_Blob")]
public class SugBlob
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)] 
    public long SUG_BlobID { get; set; }

    [StringLength(3), Unicode(false)] 
    public string Origin { get; set; } = "HQ";

    public int VesselID { get; set; }

    public long SUG_MasterID { get; set; }

    [StringLength(250)] 
    public string? FileName { get; set; }

    [StringLength(50), Unicode(false)] 
    public string? FileType { get; set; }

    public int BlobSize { get; set; }

    public byte[]? BlobContents { get; set; }

    [Column(TypeName = "datetime")] 
    public DateTime? AddedOn { get; set; }
}