using DMS.Data.EF.Models.Features;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Context;

// DbSets for the feature and vessel sync tables (DMS.Data.EF.Models.Features). All contexts use the same database.

public partial class GeneralContext
{
    public virtual DbSet<SugMaster> SugMasters { get; set; } = null!;
    public virtual DbSet<SugLog> SugLogs { get; set; } = null!;
    public virtual DbSet<SugBlob> SugBlobs { get; set; } = null!;
    public virtual DbSet<DmsInbox> DmsInbox { get; set; } = null!;
    public virtual DbSet<DmsInboxRead> DmsInboxReads { get; set; } = null!;
    public virtual DbSet<CirManualLink> CirManualLinks { get; set; } = null!;
    public virtual DbSet<BlobTableSettingRow> BlobTableSettings { get; set; } = null!;
    public virtual DbSet<SyncVessel> SyncVessels { get; set; } = null!;
    public virtual DbSet<SyncLog> SyncLogs { get; set; } = null!;
    public virtual DbSet<SyncVesselKey> SyncVesselKeys { get; set; } = null!;
    public virtual DbSet<SyncVesselItem> SyncVesselItems { get; set; } = null!;
    public virtual DbSet<SyncOutbox> SyncOutbox { get; set; } = null!;
    public virtual DbSet<SyncFullCopyItem> SyncFullCopyItems { get; set; } = null!;
    public virtual DbSet<SyncChangeLog> SyncChangeLogs { get; set; } = null!;
    /// <summary>Also mapped here so the vessel push (api/Sync/PushRows) can store folders with the other feature rows.</summary>
    public virtual DbSet<DMS.Data.EF.Models.OtherDocument.FldFolder> OtherDocumentFolders { get; set; } = null!;
    /// <summary>Also mapped here so the vessel push can store the document log rows written on board.</summary>
    public virtual DbSet<DMS.Data.EF.Models.OtherDocument.OtherDocument_Log> OtherDocumentLogs { get; set; } = null!;
}

public partial class DmsContext
{
    public virtual DbSet<NotificationRead> NotificationReads { get; set; } = null!;
}

public partial class CircularsContext
{
    public virtual DbSet<NotificationRead> NotificationReads { get; set; } = null!;
    public virtual DbSet<CirManualLink> CirManualLinks { get; set; } = null!;
}
