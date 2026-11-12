namespace DMS.Application.DTO.Sync;

// Wire contract between the office API (api/Sync/...) and each vessel's DMS.Vessel.Api.
// The vessel keeps a copy in DMS.Vessel.Infrastructure/Sync/ShoreSyncContracts.cs: change both together.
//
// Every call carries the headers X-Vessel-Id and X-Vessel-Key (see VesselKeyAttribute in the office API).

// ---- Upload (vessel -> office) -------------------------------------------------------------------------------------
// POST api/Sync/Upload (multipart/form-data): everything made or changed on board - acknowledgements, Other Documents
// and their files, folders, the document log, suggestions with their files, inbox read marks.
//   part "upload"   SyncUploadRequest as JSON
//   part(s) "file"  file contents, one per row that has FileRef; the part's file name is that FileRef
// Safe to repeat: a record the office already holds is answered as stored, never stored twice.

public class SyncUploadRequest
{
    /// <summary>Parents before children (folders, documents, then their files and log ...); the office also orders them so.</summary>
    public List<SyncUploadRow> Rows { get; set; } = new();
}

public class SyncUploadRow
{
    public string TableName { get; set; } = string.Empty;
    /// <summary>The record's key on board (Proc_NextID, this vessel's range): the office keeps it when it is free there.</summary>
    public long Key { get; set; }
    /// <summary>"U" (default): add or update the record from Row. "D": the record was removed on board (Row is empty).</summary>
    public string Action { get; set; } = "U";
    /// <summary>
    /// Every column of the record as it is on board, except the file contents (those go as a file part). Acknowledgements
    /// of circulars also carry "UserID": the crew member's user ID, which the office matches to its crew list.
    /// </summary>
    public Dictionary<string, object?> Row { get; set; } = new();
    /// <summary>The file part with this record's contents; null when none is sent (the office keeps what it holds).</summary>
    public string? FileRef { get; set; }
}

public class SyncUploadResult
{
    /// <summary>One per row sent, in the order the office handled them.</summary>
    public List<SyncUploadRowResult> Results { get; set; } = new();
}

public class SyncUploadRowResult
{
    public string TableName { get; set; } = string.Empty;
    /// <summary>The key sent (the record's key on board).</summary>
    public long Key { get; set; }
    /// <summary>The office holds the record (stored now or before): the ship stops sending it (Ship_ChangeLog).</summary>
    public bool Stored { get; set; }
    /// <summary>Why the office did not take it; the ship keeps it waiting and shows this on its Sync page.</summary>
    public string? Error { get; set; }
    /// <summary>
    /// The key the office holds the record under, when it differs from Key (taken at the office, or the office already
    /// had this record under another key): the ship replaces its record with OfficeRow under this key.
    /// </summary>
    public long? OfficeKey { get; set; }
    /// <summary>
    /// The office record as stored (no file contents), for tables whose office copy can differ from what was sent
    /// (acknowledgements, documents, document files): the ship writes it over its own.
    /// </summary>
    public Dictionary<string, object?>? OfficeRow { get; set; }
}

// ---- Row changes (office -> vessel) --------------------------------------------------------------------------------
// GET  api/Sync/Changes?max=500     the records changed at the office for this vessel (Sync_ChangeLog), each as it
//                                   is now: same table, same key, same column values.
// POST api/Sync/ChangesConfirm      the ship has saved them: their change rows get DownloadedUtc.

public class SyncChangesResponse
{
    public DateTime ServerTimeUtc { get; set; }
    /// <summary>More changes are waiting: call again straight away (after confirming these).</summary>
    public bool HasMore { get; set; }
    /// <summary>In the order the ship writes them (Sync_Table.SortOrder, parents first).</summary>
    public List<SyncRowChange> Changes { get; set; } = new();
}

public class SyncRowChange
{
    /// <summary>The Sync_ChangeLog rows this change answers (one record can have several); confirm them all.</summary>
    public List<long> ChangeIds { get; set; } = new();
    public string TableName { get; set; } = string.Empty;
    public string KeyColumn { get; set; } = string.Empty;
    public long Key { get; set; }
    /// <summary>"U" = write the record (insert or update) with Row; "D" = remove it (deleted, or no longer for this vessel).</summary>
    public string Action { get; set; } = "U";
    /// <summary>
    /// Every column of the record as the office holds it, except BlobColumn. Null for "D". Values are plain values
    /// when the office writes them and JSON elements (System.Text.Json.JsonElement) when the ship reads them.
    /// </summary>
    public Dictionary<string, object?>? Row { get; set; }
    /// <summary>The file-contents column left out of Row: the ship downloads the file in the sync's file step.</summary>
    public string? BlobColumn { get; set; }
}

public class SyncChangesConfirmRequest
{
    public List<long> ChangeIds { get; set; } = new();
}

public class SyncChangesConfirmResult
{
    public int Confirmed { get; set; }
}

// ---- Heartbeat (vessel -> office) ----------------------------------------------------------------------------------
// POST api/Sync/Heartbeat: sent every cycle, and while the ship is busy, so the office sync monitor shows the link
// state, what the ship is doing and what it still has to send.

public class SyncHeartbeatRequest
{
    public int VesselID { get; set; }
    /// <summary>Acknowledgements recorded on board and not yet sent to the office.</summary>
    public int PendingManualAcks { get; set; }
    public int PendingCircularAcks { get; set; }
    /// <summary>Other records made on board and not at the office yet (folders, documents, suggestions, ...).</summary>
    public int PendingOther { get; set; }
    /// <summary>What the ship is doing, e.g. "Downloading attachments"; null when idle.</summary>
    public string? Activity { get; set; }
    /// <summary>0-100 while busy; null when idle.</summary>
    public int? ProgressPercent { get; set; }
    public string? AppVersion { get; set; }
    /// <summary>False when the ship's last sync cycle failed (for example while applying a pull).</summary>
    public bool LastCycleSucceeded { get; set; } = true;
    public string? LastError { get; set; }
}
