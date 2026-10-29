using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Models.Features;

[Table("BlobTableSettings")]
public class BlobTableSettingRow
{
    [Key, StringLength(150), Unicode(false)] 
    public string TableName { get; set; } = "";

    [StringLength(150), Unicode(false)] 
    public string? Cloud_AccountName { get; set; }

    [StringLength(500), Unicode(false)] 
    public string? Cloud_AccountKey { get; set; }

    [StringLength(150), Unicode(false)] 
    public string? Cloud_ContainerName { get; set; }

    [StringLength(150), Unicode(false)] 
    public string? Cloud_Directory { get; set; }

    [StringLength(50)]
    public string? HQ_Storage_Option { get; set; }

    [StringLength(150)]
    public string? HQ_Local_Folder { get; set; }

    [StringLength(150)] 
    public string? HQ_DatabaseName { get; set; }

    [StringLength(50)] 
    public string? VSL_Storage_Option { get; set; }

    [StringLength(150)]
    public string? VSL_DatabaseName { get; set; }

    [StringLength(150)]
    public string? VSL_Local_Folder { get; set; }
}
