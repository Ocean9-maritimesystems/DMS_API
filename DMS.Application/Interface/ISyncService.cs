using DMS.Application.DTO.Sync;

namespace DMS.Application.Interface;

/// <summary>
/// Office side of the vessel sync. The office is the master for manuals, circulars, categories,
/// acknowledgement requirements and crew; the vessel is the master for acknowledgements.
/// </summary>
public interface ISyncService
{
    /// <summary>True when Database/005_VesselSync.sql has been run (the Sync_* tables exist).</summary>
    Task<bool> IsInstalledAsync();

    /// <summary>Checks the X-Vessel-Key sent by a vessel against Sync_VesselKey.</summary>
    Task<bool> ValidateVesselKeyAsync(int vesselId, string key);

    /// <summary>
    /// What this vessel has not received yet: new or changed items, removals, and the crew and
    /// category lists when they changed. Items sent in the previous pull (the one "since" refers to)
    /// are marked as received first.
    /// </summary>
    Task<SyncPullResponse> PullAsync(int vesselId, DateTime since);

    /// <summary>
    /// Called by the vessel after applying a pull: sets the DownloadedUtc marker on the change-table rows it
    /// downloaded, and confirms everything that pull sent as on board. Safe to repeat.
    /// </summary>
    Task<SyncConfirmResult> ConfirmAsync(SyncConfirmRequest request);

    /// <summary>Stores acknowledgements made on board. Safe to repeat: an acknowledgement already stored is skipped.</summary>
    Task<SyncPushResult> PushAsync(SyncPushRequest request);

    /// <summary>
    /// Stores one other document added on board, with its files (in the order of doc.Files). Safe to repeat: a
    /// document this vessel already sent (same type and number) is returned as it is, not stored again.
    /// </summary>
    Task<OtherDocumentPushResult> PushOtherDocumentAsync(int vesselId, OtherDocumentPushDto doc, IReadOnlyList<byte[]> files);

    /// <summary>
    /// Stores records made on board in the new feature tables (inbox read marks, suggestions, folders, files), under
    /// the same keys. Safe to repeat. Each row gets its own result, so one bad row never holds up the rest.
    /// </summary>
    Task<SyncPushRowsResult> PushRowsAsync(int vesselId, SyncPushRowsRequest request);

    /// <summary>The contents of a SUG_Blob row for this vessel (or all vessels); null when not its file.</summary>
    Task<(byte[] Bytes, string? FileName)?> GetFeatureFileAsync(int vesselId, string table, long blobId);

    /// <summary>
    /// Row-by-row sync (Database/010_Sync_ChangeLog.sql): up to <paramref name="max"/> records changed at the office
    /// for this vessel, each read as it is now by its key (same table, key and data; file contents left out).
    /// Null when 010 has not been run.
    /// </summary>
    Task<SyncChangesResponse?> GetChangesAsync(int vesselId, int max);

    /// <summary>The ship has saved these changes: sets DownloadedUtc on its Sync_ChangeLog rows. Safe to repeat.</summary>
    Task<SyncChangesConfirmResult> ConfirmChangesAsync(int vesselId, IReadOnlyCollection<long> changeIds);

    /// <summary>Records the vessel's report (pending acknowledgements, current activity, errors).</summary>
    Task HeartbeatAsync(SyncHeartbeatRequest request);

    Task<(byte[] Bytes, string FileType, string? FileName)?> GetManualAttachmentBytesAsync(int vesselId, long attachmentId);

    Task<(byte[] Bytes, string FileType, string? FileName)?> GetCircularAttachmentBytesAsync(int vesselId, long attachmentId);

    /// <summary>One OtherDocument_Blob file, only if its document belongs to this vessel (or to all vessels).</summary>
    Task<(byte[] Bytes, string FileType, string? FileName)?> GetOtherDocumentFileBytesAsync(int vesselId, long blobId);

    /// <summary>Creates (or replaces) the vessel's sync key. Returns the key; only its hash is stored.</summary>
    Task<string> GenerateVesselKeyAsync(int vesselId, string createdBy);

    /// <summary>Forgets what the vessel has received, so the next pull sends everything again.</summary>
    Task RequestResyncAsync(int vesselId);

    /// <summary>
    /// For the sync monitor: per vessel, the manuals and circulars waiting to be sent (new, changed, or to be
    /// removed), as (ItemType, ItemID) with ItemType "Manual" or "Circular". Empty when the sync tables are not installed.
    /// </summary>
    Task<Dictionary<int, HashSet<(string ItemType, long ItemId)>>> GetQueuedItemsAsync(IReadOnlyCollection<int> vesselIds);
}
