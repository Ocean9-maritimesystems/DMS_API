namespace DMS.Application.DTO.Sync;

// Wire contract between the office API (api/Sync/...) and each vessel's DMS.Vessel.Api.
// The vessel keeps a copy in DMS.Vessel.Infrastructure/Sync/ShoreSyncContracts.cs: change both together.
//
// Every call carries the headers X-Vessel-Id and X-Vessel-Key (see VesselKeyAttribute).

// ---- Pull (office -> vessel) ---------------------------------------------------------------
// GET api/Sync/Pull?vesselId={id}&since={utc}
// "since" is ServerTimeUtc from the vessel's previous pull that it applied successfully
// (DateTime.MinValue on the first sync). Items sent in that pull are then marked as received.

public class SyncPullResponse
{
    /// <summary>Send this back as "since" on the next pull, once this response has been applied.</summary>
    public DateTime ServerTimeUtc { get; set; }

    /// <summary>More items are waiting: pull again straight away (large first syncs are sent in batches).</summary>
    public bool HasMore { get; set; }

    public List<SyncManualDto> Manuals { get; set; } = new();
    /// <summary>Full list of manual categories; null when unchanged since the vessel last received it.</summary>
    public List<SyncManualCategoryDto>? ManualCategories { get; set; }
    /// <summary>Ranks that must acknowledge, for every manual in <see cref="Manuals"/> (replaces what the vessel has for those manuals).</summary>
    public List<SyncManualAckRequirementDto> ManualAckRequirements { get; set; } = new();
    public List<long> DeletedManualIds { get; set; } = new();

    public List<SyncCircularDto> Circulars { get; set; } = new();
    /// <summary>Full list of circular categories; null when unchanged.</summary>
    public List<SyncCircularCategoryDto>? CircularCategories { get; set; }
    /// <summary>Ranks that must acknowledge, for every circular in <see cref="Circulars"/>.</summary>
    public List<SyncCircularAckRequirementDto> CircularAckRequirements { get; set; } = new();
    public List<long> DeletedCircularIds { get; set; } = new();

    /// <summary>Full crew list for the vessel; null when unchanged.</summary>
    public List<SyncCrewDto>? Crew { get; set; }

    /// <summary>
    /// Only on a first sync or after "Force resync" from the office: every manual and circular the
    /// vessel should have. The vessel removes anything else it holds. Null otherwise.
    /// </summary>
    public List<long>? AllManualIds { get; set; }
    public List<long>? AllCircularIds { get; set; }

    /// <summary>
    /// The change-table rows (Sync_Outbox) this response delivers. Once the response is applied, the vessel
    /// records them on board and sends their IDs to api/Sync/Confirm, which sets their DownloadedUtc marker.
    /// </summary>
    public List<SyncOutboxRowDto> SyncRows { get; set; } = new();

    // Fixed tables, copied whole (every row, no filter). Null when unchanged since the vessel last received them.
    /// <summary>DM_Status: the manual status list.</summary>
    public List<SyncDmStatusDto>? DmStatuses { get; set; }
    /// <summary>DM_Settings: the manual settings.</summary>
    public List<SyncDmSettingDto>? DmSettings { get; set; }
    /// <summary>CIR_Settings: the circular settings.</summary>
    public List<SyncCirSettingDto>? CirSettings { get; set; }
    /// <summary>V_Vessel: the vessel list.</summary>
    public List<SyncVesselDto>? Vessels { get; set; }

    /// <summary>This vessel's other documents (OtherDocument_Type, OtherDocumentMaster, OtherDocument_Blob); null when unchanged.</summary>
    public SyncOtherDocumentsDto? OtherDocuments { get; set; }
}

/// <summary>One row of DM_Status at the office.</summary>
public class SyncDmStatusDto
{
    public long DM_StatusID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string? StatusCode { get; set; }
    public string StatusName { get; set; } = string.Empty;
    public int? IsDisabled { get; set; }
    public int? IsVisible { get; set; }
    public int? SortOrder { get; set; }
}

/// <summary>One row of DM_Settings at the office.</summary>
public class SyncDmSettingDto
{
    public long DM_SettingsID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string SettingName { get; set; } = string.Empty;
    public string? SettingValue { get; set; }
}

/// <summary>One row of CIR_Settings at the office.</summary>
public class SyncCirSettingDto
{
    public long CIR_SettingsID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string SettingName { get; set; } = string.Empty;
    public string? SettingValue { get; set; }
}

/// <summary>One row of V_Vessel at the office.</summary>
public class SyncVesselDto
{
    public long V_VesselID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string VslCode { get; set; } = string.Empty;
    public string VslName { get; set; } = string.Empty;
    public string? VslType { get; set; }
    public DateTime? DelivDate { get; set; }
    public string? BaseCurrency { get; set; }
    public string? VslEmailAddress { get; set; }
    public string? OperateBy { get; set; }
    public int? IsRetired { get; set; }
    /// <summary>First number of the vessel's ID range for Proc_NextID (null when the office has no TableIDstart column).</summary>
    public long? TableIDstart { get; set; }
}

/// <summary>One row of the office change table (Sync_Outbox): what changed at HQ for this vessel.</summary>
public class SyncOutboxRowDto
{
    public long SyncOutboxID { get; set; }
    /// <summary>Manual | Circular | Crew | Categories</summary>
    public string ItemType { get; set; } = string.Empty;
    /// <summary>DM_ManualID / CIR_MasterID; 0 for Crew and Categories.</summary>
    public long ItemID { get; set; }
    /// <summary>I = inserted, U = updated, D = deleted at HQ.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>When the change was made at HQ (UTC).</summary>
    public DateTime ChangedUtc { get; set; }
}

/// <summary>Sent by the vessel after it has applied a pull: marks those change-table rows as downloaded.</summary>
public class SyncConfirmRequest
{
    /// <summary>Set by the office from the vessel's headers; any value sent is ignored.</summary>
    public int VesselID { get; set; }
    /// <summary>ServerTimeUtc of the pull that was applied. Everything that pull sent is confirmed as on board.</summary>
    public DateTime ServerTimeUtc { get; set; }
    /// <summary>SyncOutboxID of every change-table row the vessel downloaded.</summary>
    public List<long> SyncOutboxIds { get; set; } = new();
}

public class SyncConfirmResult
{
    /// <summary>Change-table rows marked as downloaded by this call (rows already marked are not counted).</summary>
    public int Confirmed { get; set; }
}

/// <summary>A manual as the office holds it: its DM_Manual row with the related rows the vessel keeps.</summary>
public class SyncManualDto
{
    public long DM_ManualID { get; set; }
    public SyncDMManualRow Manual { get; set; } = new();
    /// <summary>The current released version (DM_ManualVersion.IsActive = 1).</summary>
    public SyncDMManualVersionRow? Version { get; set; }
    /// <summary>File details only; the files are downloaded separately (api/Sync/ManualAttachment/{id}).</summary>
    public List<SyncDMManualAttachmentsRow> Attachments { get; set; } = new();
    /// <summary>DM_Manual_ToAck_Vsl rows for this vessel and for all vessels (-1). Replaces what the vessel holds.</summary>
    public List<SyncDMManualToAckVslRow> ToAck { get; set; } = new();
    /// <summary>This vessel's DM_Manual_UserAck rows for the current version.</summary>
    public List<SyncDMManualUserAckRow> UserAcks { get; set; } = new();
}

/// <summary>One DM_Category row.</summary>
public class SyncManualCategoryDto
{
    public long DM_CategoryID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string? CategoryCode { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public int? IsDisabled { get; set; }
}

public class SyncManualAckRequirementDto
{
    public long DM_ManualID { get; set; }
    public string UserRank { get; set; } = string.Empty;
}

/// <summary>A circular or alert as the office holds it: its CIR_Master row with the related rows the vessel keeps.</summary>
public class SyncCircularDto
{
    public long CIR_MasterID { get; set; }
    public SyncCIRMasterRow Master { get; set; } = new();
    /// <summary>File details only; the files are downloaded separately (api/Sync/CircularAttachment/{id}).</summary>
    public List<SyncCIRBlobRow> Blobs { get; set; } = new();
    /// <summary>CIR_ToAck_Vsl rows for this vessel and for all vessels (-1). Replaces what the vessel holds.</summary>
    public List<SyncCIRToAckVslRow> ToAck { get; set; } = new();
    /// <summary>This vessel's CIR_UserAck rows.</summary>
    public List<SyncCIRUserAckRow> UserAcks { get; set; } = new();
}

/// <summary>One CIR_Category row.</summary>
public class SyncCircularCategoryDto
{
    public long CIR_CategoryID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string CircularType { get; set; } = string.Empty;
    public string CategoryCode { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int? isActive { get; set; }
    public string? Sortorder { get; set; }
    /// <summary>Same as Category (kept for older ship servers).</summary>
    public string? CategoryName { get; set; }
}

public class SyncCircularAckRequirementDto
{
    public long CIR_MasterID { get; set; }
    public string UserRank { get; set; } = string.Empty;
}

public class SyncAttachmentRefDto
{
    public long AttachmentID { get; set; }
    /// <summary>Manual | Circular</summary>
    public string Source { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? FileType { get; set; }
    public int BlobSize { get; set; }
}

public class SyncCrewDto
{
    /// <summary>Vsl_Staff.Vsl_StaffID at the office: the Fleet_CrewID in CIR_UserAck.</summary>
    public long VslStaffId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string UserRank { get; set; } = string.Empty;
    public string? UserDept { get; set; }
    public int? IsDisabled { get; set; }
    /// <summary>Vsl_Staff.Password, so crew can log in on board without the link. The vessel stores only a hash.</summary>
    public string? Password { get; set; }
}

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

/// <summary>One row of DM_Category.</summary>
public class SyncDMCategoryRow
{
    public long DM_CategoryID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string? CategoryCode { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public int? IsDisabled { get; set; }
}

/// <summary>One row of DM_Manual.</summary>
public class SyncDMManualRow
{
    public long DM_ManualID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long? DM_CategoryID { get; set; }
    public string ManualNo { get; set; } = string.Empty;
    public string? ManualCode { get; set; }
    public string? ManualName { get; set; }
    public long DM_StatusID { get; set; }
    public long ParentId { get; set; }
    public int ManualLevel { get; set; }
    public int? IsMainTitle { get; set; }
    public int? IsAcknowledgementRequired { get; set; }
    public int CanExport { get; set; }
    public int IsApprovalRequired { get; set; }
    public int? Approval_FlowID { get; set; }
    public string? NextApproval_HQRole { get; set; }
    public string? NextApproval_HQDept { get; set; }
    public string? SortOrder { get; set; }
    public string? AddlSortOrder { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public int? IsDeleted { get; set; }
}

/// <summary>One row of DM_ManualVersion.</summary>
public class SyncDMManualVersionRow
{
    public long DM_ManualVersionID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long DM_ManualID { get; set; }
    public string Version { get; set; } = string.Empty;
    public string? TextContents { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTime? ReleasedDate { get; set; }
    public int? IsActive { get; set; }
    public int? IsDisabled { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTime? LastModifiedDate { get; set; }
    public string? Comments { get; set; }
}

/// <summary>One row of DM_Manual_Attachments.</summary>
public class SyncDMManualAttachmentsRow
{
    public long DM_Manual_AttachmentsID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long DM_ManualID { get; set; }
    public int? ImageRefNo { get; set; }
    public int BlobSize { get; set; }
    public string FileType { get; set; } = string.Empty;
    public DateTime? AddedOn { get; set; }
    public int? IsDeleted { get; set; }
}

/// <summary>One row of DM_Manual_ToAck_Vsl.</summary>
public class SyncDMManualToAckVslRow
{
    public long DM_Manual_ToAck_VslID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long DM_ManualID { get; set; }
    public string UserRank { get; set; } = string.Empty;
    public int? IsActive { get; set; }
}

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

/// <summary>One row of CIR_Category.</summary>
public class SyncCIRCategoryRow
{
    public long CIR_CategoryID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string CircularType { get; set; } = string.Empty;
    public string CategoryCode { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int? isActive { get; set; }
    public string? Sortorder { get; set; }
}

/// <summary>One row of CIR_Master.</summary>
public class SyncCIRMasterRow
{
    public long CIR_MasterID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string CircularType { get; set; } = string.Empty;
    public long? CIR_CategoryID { get; set; }
    public string? CIR_Number { get; set; }
    public string? Reference { get; set; }
    public string? Title { get; set; }
    public string? Status { get; set; }
    public int? Priority { get; set; }
    public DateTime? DateIssued { get; set; }
    public string? Remarks { get; set; }
    public DateTime? ReleasedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public int? IsDeleted { get; set; }
}

/// <summary>One row of CIR_Blob.</summary>
public class SyncCIRBlobRow
{
    public long CIR_BlobID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long? CIR_MasterID { get; set; }
    public string? Description { get; set; }
    public string? FileName { get; set; }
    public string? FileType { get; set; }
    public string? OriginalFileType { get; set; }
    public int? BlobSize { get; set; }
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

/// <summary>One row of CIR_ToAck_Vsl.</summary>
public class SyncCIRToAckVslRow
{
    public long CIR_ToAck_VslID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public long CIR_MasterID { get; set; }
    public string UserRank { get; set; } = string.Empty;
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

/// <summary>
/// This vessel's other documents, as the office holds them: every OtherDocument_Type row, and the
/// OtherDocumentMaster rows for this vessel (or all vessels, -1) with their OtherDocument_Blob rows
/// (file details only; the files are downloaded separately, api/Sync/OtherDocumentFile/{id}).
/// Sent whole, and only when something changed; it replaces what the vessel holds.
/// </summary>
public class SyncOtherDocumentsDto
{
    public List<SyncOtherDocumentTypeRow> Types { get; set; } = new();
    public List<SyncOtherDocumentMasterRow> Documents { get; set; } = new();
    public List<SyncOtherDocumentBlobRow> Files { get; set; } = new();
}

/// <summary>One row of OtherDocument_Type.</summary>
public class SyncOtherDocumentTypeRow
{
    public long OtherDocument_TypeID { get; set; }
    public string Origin { get; set; } = string.Empty;
    public int VesselID { get; set; }
    public string DocumentCode { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public int? IsActive { get; set; }
    public int SortOrder { get; set; }
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
