using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Models.Features;

[Table("Sync_Vessel")]
public class SyncVessel
{
    /// <summary>Not an identity column: the next number comes from dbo.Proc_NextID (TableIdExtensions).</summary>
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long Sync_VesselID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = "";

    public int VesselID { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime? LastContactUtc { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime? LastSuccessUtc { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime? LastPullUtc { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime? LastPushUtc { get; set; }

    [StringLength(20)] 
    public string? LastStatus { get; set; }

    [StringLength(1000)] 
    public string? LastError { get; set; }

    public int PendingOnShip { get; set; }

    [StringLength(100)] 
    public string? CurrentActivity { get; set; }

    public int? ProgressPercent { get; set; }

    [StringLength(50)] 
    public string? AppVersion { get; set; }
}

[Table("Sync_Log")]
public class Sync_Log
{
    /// <summary>Not an identity column: the next number comes from dbo.Proc_NextID (TableIdExtensions).</summary>
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long Sync_LogID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = "";

    public int VesselID { get; set; }

    [StringLength(10)] 
    public string Direction { get; set; } = "";

    [Column(TypeName = "datetime2(3)")] 
    public DateTime StartedUtc { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime? FinishedUtc { get; set; }

    public int ItemsCount { get; set; }

    [StringLength(20)]
    public string Status { get; set; } = "";

    [StringLength(1000)] 
    public string? Error { get; set; }
}

[Table("Sync_VesselKey")]
public class SyncVesselKey
{
    /// <summary>Not an identity column: the next number comes from dbo.Proc_NextID (TableIdExtensions).</summary>
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long Sync_VesselKeyID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = "";

    public int VesselID { get; set; }

    [Column(TypeName = "char(64)")]
    public string KeyHash { get; set; } = "";

    public bool IsActive { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime CreatedUtc { get; set; }

    [StringLength(150)] 
    public string? CreatedBy { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime? LastUsedUtc { get; set; }
}

[Table("Sync_ChangeLog")]
public class SyncChangeLog
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)] 
    public long Sync_ChangeLogID { get; set; }

    [StringLength(3)]
    public string Origin { get; set; } = "";

    public int VesselID { get; set; }

    [StringLength(128), Unicode(false)] 
    public string TableName { get; set; } = "";

    public long KeyValue { get; set; }

    [Column(TypeName = "char(1)")] 
    public string Action { get; set; } = "";

    [Column(TypeName = "datetime2(3)")] 
    public DateTime CreatedUtc { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime? SentUtc { get; set; }

    [Column(TypeName = "datetime2(3)")] 
    public DateTime? DownloadedUtc { get; set; }
}

// ------------------------------------------------------------------------------------------------ Results of stored procedures

/// <summary>dbo.Api_SyncTableColumns: one row per column of each active synced table (Sync_Table).</summary>
[Keyless]
public class SyncTableColumnRow
{
    public string TableName { get; set; } = "";
    public int SortOrder { get; set; }
    public string? BlobColumn { get; set; }
    public bool AllVessels { get; set; }
    public string? KeyColumn { get; set; }
    public bool HasVesselColumn { get; set; }
    public string ColumnName { get; set; } = "";
}
