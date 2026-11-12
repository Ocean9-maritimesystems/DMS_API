namespace DMS.Application.DTO;

/// <summary>One manual or circular/alert assigned to a vessel.</summary>
public class VesselSyncItemDto
{
    /// <summary>Manual | Circular | Alert</summary>
    public string ItemType { get; set; } = string.Empty;

    public long ItemID { get; set; }
    
    public string? Number { get; set; }
    
    public string? Title { get; set; }
    
    public string? Version { get; set; }
    
    public string? Category { get; set; }
    
    public DateTime? IssuedOn { get; set; }
    
    public int RanksRequired { get; set; }
    
    public int RanksAcknowledged { get; set; }
    
    public DateTime? LastReadOn { get; set; }
    
    public string Status { get; set; } = string.Empty;

    public string? Delivery { get; set; }
}

/// <summary>Sync calls for a vessel (dbo.Sync_Log), newest first, and the state of its sync key.</summary>
public class VesselSyncHistoryDto
{
    public bool SyncInstalled { get; set; }
    
    public bool HasKey { get; set; }
    
    public DateTime? KeyCreatedUtc { get; set; }
    
    public string? KeyCreatedBy { get; set; }
    
    public DateTime? KeyLastUsedUtc { get; set; }

    public string? KeyError { get; set; }
    
    public List<VesselSyncLogDto> Entries { get; set; } = new();
}

public class VesselSyncLogDto
{
    public long Sync_LogID { get; set; }

    /// <summary>Down (office to vessel) | Up (vessel to office)</summary>
    public string Direction { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? FinishedUtc { get; set; }

    public int ItemsCount { get; set; }

    /// <summary>OK | Failed</summary>
    public string Status { get; set; } = string.Empty;

    public string? Error { get; set; }
}

/// <summary>Returned once by Vessel/GenerateSyncKey: the key is not stored and cannot be shown again.</summary>
public class VesselSyncKeyDto
{
    public int VesselID { get; set; }

    public string Key { get; set; } = string.Empty;
}

/// <summary>Sync monitor: one row per vessel, for the Vessel Sync Status dashboard tab.</summary>
public class VesselSyncMonitorDto
{
    public bool SyncInstalled { get; set; }

    public int OnlineMinutes { get; set; }

    public List<VesselSyncMonitorRow> Vessels { get; set; } = new();
}

public class VesselSyncMonitorRow
{
    public int VesselID { get; set; }

    public string? VslCode { get; set; }

    public string? VslName { get; set; }

    public string? VslType { get; set; }

    public string? VslEmail { get; set; }

    public string Link { get; set; } = "Not connected";

    public DateTime? LastContactUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public string? LastStatus { get; set; }

    public string? LastError { get; set; }

    public int ItemsQueued { get; set; }

    public int ChangesWaiting { get; set; }

    public int ItemsTotal { get; set; }

    public int AlertsPending { get; set; }

    public int AcksPending { get; set; }
    
    public int PendingOnShip { get; set; }
    
    public string? CurrentActivity { get; set; }
    
    public int? ProgressPercent { get; set; }
    
    public string? AppVersion { get; set; }
    
    public bool HasKey { get; set; }
}
