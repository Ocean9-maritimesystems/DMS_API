namespace DMS.Application.DTO.Sync;

// Wire contract between the office API (api/Sync/...) and each vessel's DMS.Vessel.Api.
// The vessel keeps a copy in DMS.Vessel.Infrastructure/Sync/ShoreSyncContracts.cs: change both together.
//
// Every call carries the headers X-Vessel-Id and X-Vessel-Key (see VesselKeyAttribute).

// ---- Push (vessel -> office) ---------------------------------------------------------------
// POST api/Sync/Push

public class SyncPushRequest
{
    public int VesselID { get; set; }
    public List<ManualAckPushDto> ManualAcks { get; set; } = new();
    public List<CircularAckPushDto> CircularAcks { get; set; } = new();
}

public class ManualAckPushDto
{
    /// <summary>The ID on board (Proc_NextID, this vessel's range): the office stores the row under it when free. Negative from older ships: the office numbers it.</summary>
    public long DM_Manual_UserAckID { get; set; }
    public long DM_ManualID { get; set; }
    public long DM_ManualVersionID { get; set; }
    public int Version { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? UserRank { get; set; }
    public string? UserName { get; set; }
    /// <summary>UTC.</summary>
    public DateTime DateRead { get; set; }
    public string? Comments { get; set; }
}

public class CircularAckPushDto
{
    /// <summary>The ID on board, as for ManualAckPushDto.</summary>
    public long CIR_UserAckID { get; set; }
    public long CIR_MasterID { get; set; }
    /// <summary>Vsl_Staff.UserID; stored as Fleet_CrewID = Vsl_Staff.Vsl_StaffID.</summary>
    public string UserId { get; set; } = string.Empty;
    /// <summary>UTC.</summary>
    public DateTime DateRead { get; set; }
    public string? Comments { get; set; }
}

public class SyncPushResult
{
    /// <summary>The office rows for the manual acknowledgements in the request (new or already stored), with their office IDs.</summary>
    public List<SyncDMManualUserAckRow> ManualAckRows { get; set; } = new();
    /// <summary>The office rows for the circular acknowledgements in the request.</summary>
    public List<SyncCIRUserAckRow> CircularAckRows { get; set; } = new();
    public int ManualAcksAccepted { get; set; }
    public int ManualAcksAlreadyApplied { get; set; }
    public int CircularAcksAccepted { get; set; }
    public int CircularAcksAlreadyApplied { get; set; }
    public List<string> Warnings { get; set; } = new();
    /// <summary>Acknowledgements not stored (unknown crew member, manual version or circular), as "{DM_ManualVersionID}:{UserId}".
    /// The vessel keeps them and sends them again, e.g. once the crew list ashore is corrected.</summary>
    public List<string> SkippedManualAcks { get; set; } = new();
    /// <summary>As above, "{CIR_MasterID}:{UserId}".</summary>
    public List<string> SkippedCircularAcks { get; set; } = new();
}

// ---- Row changes (office -> vessel) --------------------------------------------------------
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
    /// <summary>Every column of the record as the office holds it, except BlobColumn. Null for "D".</summary>
    public Dictionary<string, object?>? Row { get; set; }
    /// <summary>The file-contents column left out of Row: the ship downloads the file when it is first opened.</summary>
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

// ---- Other documents added on board (vessel -> office) -------------------------------------
// POST api/Sync/OtherDocument (multipart/form-data, one document per call): a "document" part holding
// OtherDocumentPushDto as JSON, then one "files" part per file, in the same order as Document.Files.
// Safe to repeat: a document this vessel already sent (same type and number) is not stored twice.

public class OtherDocumentPushDto
{
    /// <summary>The document's ID on board (Proc_NextID, this vessel's range): the office stores it under the same ID when free.</summary>
    public long VesselDocumentID { get; set; }
    public long OtherDocument_TypeID { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string DocumentTitle { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? Reference { get; set; }
    public string? Comments { get; set; }
    public DateTime? UploadedOn { get; set; }
    public string? UploadedBy { get; set; }
    public List<OtherDocumentPushFileDto> Files { get; set; } = new();

    /// <summary>The folder the document was filed in on board (OtherDocument_Folder, same ID at the office).</summary>
    public long? OtherDocument_FolderID { get; set; }
}

/// <summary>
/// POST api/Sync/PushRows: records made on board in the new feature tables (Database/012_NewFeatures.sql), sent
/// as they are, under the same key. Only the tables in SyncService.PushableTables; Origin must be VSL and VesselID the
/// calling vessel. Personal folders and their files are never sent.
/// </summary>
public class SyncPushRowsRequest
{
    public List<SyncPushRow> Rows { get; set; } = new();
}

public class SyncPushRow
{
    public string TableName { get; set; } = string.Empty;
    public long Key { get; set; }
    /// <summary>"U" (default): add or update the record from Row. "D": the record was removed on board (Row is empty).</summary>
    public string Action { get; set; } = "U";
    /// <summary>Every column of the record (file contents as base64).</summary>
    public Dictionary<string, System.Text.Json.JsonElement> Row { get; set; } = new();
}

public class SyncPushRowsResult
{
    public List<SyncPushRowResult> Results { get; set; } = new();
}

public class SyncPushRowResult
{
    public string TableName { get; set; } = string.Empty;
    public long Key { get; set; }
    /// <summary>The office holds the record (stored now or before): the ship stops sending it (Ship_ChangeLog).</summary>
    public bool Stored { get; set; }
    public string? Error { get; set; }
}

public class OtherDocumentPushFileDto
{
    /// <summary>The file's ID on board: kept by the office when free, as the document's.</summary>
    public long VesselFileID { get; set; }
    public string? FileName { get; set; }
    public string? Description { get; set; }
    public string? FileType { get; set; }
    public string? OriginalFileType { get; set; }
    public int BlobSize { get; set; }
    public DateTime? AddedOn { get; set; }
}

public class OtherDocumentPushResult
{
    /// <summary>The office holds the document (just stored, or stored by an earlier call).</summary>
    public bool Stored { get; set; }
    public bool AlreadyApplied { get; set; }
    /// <summary>Why the office refused it (unknown type, missing number ...). The vessel keeps it and shows this.</summary>
    public string? Error { get; set; }
    /// <summary>The office row, with its office ID.</summary>
    public SyncOtherDocumentMasterRow? Document { get; set; }
    /// <summary>The office file rows. VesselFileIDs[i] is the on-board ID of Files[i].</summary>
    public List<SyncOtherDocumentBlobRow> Files { get; set; } = new();
    public List<long> VesselFileIDs { get; set; } = new();
}

// ---- Heartbeat (vessel -> office) ----------------------------------------------------------
// POST api/Sync/Heartbeat: sent every cycle, and while the ship is busy, so the office
// sync monitor shows the link state, what the ship is doing and what it still has to send.

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

// ---- Office table rows, same columns as the tables (file contents excepted: downloaded separately) ----

/// <summary>One row of DM_Manual_UserAck.</summary>
public class SyncDMManualUserAckRow
{
    public long DM_Manual_UserAckID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long DM_ManualID { get; set; }
    public long DM_ManualVersionID { get; set; }
    public int Version { get; set; }
    public string? UserRank { get; set; }
    public string? UserName { get; set; }
    public string UserID { get; set; } = string.Empty;
    public DateTime? DateRead { get; set; }
    public string? Comments { get; set; }
    public int? IsActive { get; set; }
}

/// <summary>One row of CIR_UserAck.</summary>
public class SyncCIRUserAckRow
{
    public long CIR_UserAckID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long? CIR_MasterID { get; set; }
    public long? Fleet_CrewID { get; set; }
    public DateTime? DateRead { get; set; }
    public string? Comments { get; set; }
    public int? IsActive { get; set; }
}

/// <summary>One row of OtherDocumentMaster.</summary>
public class SyncOtherDocumentMasterRow
{
    public long OtherDocumentMasterID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long? OtherDocument_TypeID { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string DocumentTitle { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? Reference { get; set; }
    public string? Comments { get; set; }
    public DateTime? UploadedOn { get; set; }
    public string? UploadedBy { get; set; }
    public int? IsDeleted { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>One row of OtherDocument_Blob.</summary>
public class SyncOtherDocumentBlobRow
{
    public long OtherDocument_BlobID { get; set; }
    public long OtherDocumentMasterID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string? Description { get; set; }
    public string? FileName { get; set; }
    public string? FileType { get; set; }
    public string? OriginalFileType { get; set; }
    public int BlobSize { get; set; }
    public DateTime? AddedOn { get; set; }
    public string? Cloud_ContainerName { get; set; }
    public string? Cloud_Directory { get; set; }
    public string? HQ_Storage_Option { get; set; }
    public string? HQ_DatabaseName { get; set; }
    public string? HQ_Local_Folder { get; set; }
    public string? VSL_Storage_Option { get; set; }
    public string? VSL_DatabaseName { get; set; }
    public string? VSL_Local_Folder { get; set; }
}
