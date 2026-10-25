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
    /// <summary>Ranks on board that must acknowledge (0 = no acknowledgement needed).</summary>
    public int RanksRequired { get; set; }
    public int RanksAcknowledged { get; set; }
    public DateTime? LastReadOn { get; set; }
    /// <summary>Acknowledged | Partly acknowledged | Pending | Sent</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>On board (the vessel has this version) | Queued (waiting to be sent) | null when vessel sync is not set up.</summary>
    public string? Delivery { get; set; }
}

/// <summary>Sync calls for a vessel (dbo.Sync_Log), newest first, and the state of its sync key.</summary>
public class VesselSyncHistoryDto
{
    /// <summary>False until Database/005_VesselSync.sql has been run.</summary>
    public bool SyncInstalled { get; set; }
    /// <summary>A sync key has been generated for the vessel (the key itself is never shown again).</summary>
    public bool HasKey { get; set; }
    public DateTime? KeyCreatedUtc { get; set; }
    public string? KeyCreatedBy { get; set; }
    public DateTime? KeyLastUsedUtc { get; set; }
    /// <summary>Why the key could not be read (the history is still returned); null when it was read.</summary>
    public string? KeyError { get; set; }
    public List<VesselSyncLogDto> Entries { get; set; } = new();
}

public class VesselSyncLogDto
{
    public long SyncLogID { get; set; }
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
    /// <summary>False until Database/005_VesselSync.sql has been run; link data is then unknown.</summary>
    public bool SyncInstalled { get; set; }
    /// <summary>A vessel counts as online when it contacted HQ within this many minutes.</summary>
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
    /// <summary>Online | Offline | Not connected</summary>
    public string Link { get; set; } = "Not connected";
    public DateTime? LastContactUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    /// <summary>Manuals and circulars waiting to go to the vessel: new, changed, or to be removed.</summary>
    public int ItemsQueued { get; set; }
    /// <summary>Items assigned to the vessel in total.</summary>
    public int ItemsTotal { get; set; }
    /// <summary>Alerts the vessel has not fully acknowledged.</summary>
    public int AlertsPending { get; set; }
    /// <summary>Manuals, circulars and alerts the vessel has not fully acknowledged.</summary>
    public int AcksPending { get; set; }
    /// <summary>Acknowledgements made on board that the vessel has not sent to the office yet (reported by the vessel).</summary>
    public int PendingOnShip { get; set; }
    /// <summary>What the vessel reports it is doing (e.g. "Downloading attachments"); null when idle.</summary>
    public string? CurrentActivity { get; set; }
    /// <summary>0-100 while the vessel is busy; null when idle.</summary>
    public int? ProgressPercent { get; set; }
    public string? AppVersion { get; set; }
    /// <summary>A sync key has been generated for the vessel.</summary>
    public bool HasKey { get; set; }
}
