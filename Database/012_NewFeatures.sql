/* =====================================================================================================
   012_NewFeatures.sql  -  run on the OFFICE (HQ) database, after 009, 010 and 011. Safe to run again.

   Tables, columns, settings and triggers for the eight features in "Marine DMS – Next Features Requirements":

     1  Pending-acknowledgement dashboards  AckDueDays on DM_Manual / CIR_Master (filled from the settings when a
                                            record is added); settings AllowedRolesToViewAllRankPendingItems,
                                            DefaultAckDueDays (manual), DefaultAckDueDays_Circular / _Alert
     2  New-items inbox                     DMS_Inbox (written by triggers when an item is released / added /
                                            changed), DMS_InboxRead (who read what)
     3  Circular <-> manual link            CIR_ManualLink
     4  Suggestions                         SUG_Master, SUG_Log, SUG_Blob
     5  Folders for Other Documents         FLD_Folder (documents: OtherDocumentMaster.FLD_FolderID, 013);
                                            setting FLD_MaxFileSizeMB (largest file on a document)
     6  Certificates (on Other Documents)   OtherDocument_Type.IsCertificate / WarningDays;
                                            OtherDocumentMaster.CertificateNo / IssuedBy / IssueDate / ExpiryDate /
                                            NextSurveyDate / ReplacesID
     7  Circulars                           CIR_Master.ValidUntil / SupersededByID (Status 'Cancelled');
                                            CIR_Category.RequiresValidUntil
     8  Global search                       no tables (searches the tables above)

   VesselID on the new tables:  a vessel = that vessel only   -1 = all vessels   -2 = office only (never synced)
   Sync_LogChanges / Sync_QueueFullCopy (010) are updated here to honour -2.
   ===================================================================================================== */

SET NOCOUNT ON;
GO

/* ------------------------------------------------------------------------ Helpers ---- */

/* Adds a column when it is not there yet. */
CREATE OR ALTER PROCEDURE dbo.Feat_AddColumn @Table sysname, @Column sysname, @Definition nvarchar(400)
AS
BEGIN
    SET NOCOUNT ON;
    IF OBJECT_ID(N'dbo.' + QUOTENAME(@Table), N'U') IS NULL
    BEGIN PRINT 'Skipped ' + @Table + '.' + @Column + ': no such table.'; RETURN; END;
    IF COL_LENGTH(N'dbo.' + QUOTENAME(@Table), @Column) IS NOT NULL RETURN;
    DECLARE @sql nvarchar(max) = N'ALTER TABLE dbo.' + QUOTENAME(@Table) + N' ADD ' + QUOTENAME(@Column) + N' ' + @Definition + N';';
    EXEC sp_executesql @sql;
    PRINT 'Added ' + @Table + '.' + @Column;
END
GO

/* Adds a setting row (Origin HQ, VesselID -1) to DM_Settings or CIR_Settings when it is not there yet. */
CREATE OR ALTER PROCEDURE dbo.Feat_AddSetting @Table sysname, @Name varchar(250), @Value varchar(250)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @key sysname = CASE @Table WHEN 'DM_Settings' THEN 'DM_SettingsID' WHEN 'CIR_Settings' THEN 'CIR_SettingsID' END;
    DECLARE @exists int, @sql nvarchar(max), @id bigint;
    SET @sql = N'SELECT @e = COUNT(*) FROM dbo.' + QUOTENAME(@Table) + N' WHERE SettingName = @n';
    EXEC sp_executesql @sql, N'@n varchar(250), @e int OUTPUT', @n = @Name, @e = @exists OUTPUT;
    IF @exists > 0 RETURN;
    EXEC dbo.Proc_NextID @TableName = @Table, @Origin = 'HQ', @VesselID = -1, @Incrementor = 1, @NewTableID = @id OUTPUT;
    SET @sql = N'INSERT dbo.' + QUOTENAME(@Table) + N' (' + QUOTENAME(@key) + N', Origin, VesselID, SettingName, SettingValue)
                 VALUES (@id, ''HQ'', -1, @n, @v)';
    EXEC sp_executesql @sql, N'@id bigint, @n varchar(250), @v varchar(250)', @id = @id, @n = @Name, @v = @Value;
    PRINT 'Setting ' + @Name + ' = ' + @Value;
END
GO

/* ------------------------------------------------------------- 1. Acknowledgement due days ---- */

EXEC dbo.Feat_AddColumn 'DM_Manual',  'AckDueDays', 'int NULL';
EXEC dbo.Feat_AddColumn 'CIR_Master', 'AckDueDays', 'int NULL';
EXEC dbo.Feat_AddSetting 'DM_Settings', 'AllowedRolesToViewAllRankPendingItems', 'Master,Chief Engineer';
EXEC dbo.Feat_AddSetting 'DM_Settings', 'DefaultAckDueDays', '7';
EXEC dbo.Feat_AddSetting 'CIR_Settings', 'DefaultAckDueDays_Circular', '7';
EXEC dbo.Feat_AddSetting 'CIR_Settings', 'DefaultAckDueDays_Alert', '1';
GO

/* A record added without AckDueDays takes it from the settings ("while adding, read the value from the settings"). */
CREATE OR ALTER TRIGGER dbo.TR_DM_Manual_AckDueDays ON dbo.DM_Manual AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted WHERE AckDueDays IS NULL) RETURN;
    DECLARE @days int = TRY_CAST((SELECT TOP (1) SettingValue FROM dbo.DM_Settings WHERE SettingName = 'DefaultAckDueDays') AS int);
    UPDATE m SET AckDueDays = ISNULL(@days, 7)
      FROM dbo.DM_Manual m JOIN inserted i ON i.DM_ManualID = m.DM_ManualID
     WHERE m.AckDueDays IS NULL;
END
GO

CREATE OR ALTER TRIGGER dbo.TR_CIR_Master_AckDueDays ON dbo.CIR_Master AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted WHERE AckDueDays IS NULL) RETURN;
    DECLARE @circular int = TRY_CAST((SELECT TOP (1) SettingValue FROM dbo.CIR_Settings WHERE SettingName = 'DefaultAckDueDays_Circular') AS int),
            @alert    int = TRY_CAST((SELECT TOP (1) SettingValue FROM dbo.CIR_Settings WHERE SettingName = 'DefaultAckDueDays_Alert') AS int);
    UPDATE c SET AckDueDays = CASE WHEN c.CircularType LIKE 'Alert%' THEN ISNULL(@alert, 1) ELSE ISNULL(@circular, 7) END
      FROM dbo.CIR_Master c JOIN inserted i ON i.CIR_MasterID = c.CIR_MasterID
     WHERE c.AckDueDays IS NULL;
END
GO

/* ------------------------------------------------------------------------- 7. Circulars ---- */

EXEC dbo.Feat_AddColumn 'CIR_Master',   'ValidUntil',        'datetime NULL';
EXEC dbo.Feat_AddColumn 'CIR_Master',   'SupersededByID',    'bigint NULL';
EXEC dbo.Feat_AddColumn 'CIR_Category', 'RequiresValidUntil','int NOT NULL CONSTRAINT DF_CIR_Category_RequiresValidUntil DEFAULT (0)';
GO

/* ------------------------------------------------------------------------ 6. Certificates ---- */

EXEC dbo.Feat_AddColumn 'OtherDocument_Type',  'IsCertificate', 'int NOT NULL CONSTRAINT DF_OtherDocument_Type_IsCertificate DEFAULT (0)';
EXEC dbo.Feat_AddColumn 'OtherDocument_Type',  'WarningDays',   'int NOT NULL CONSTRAINT DF_OtherDocument_Type_WarningDays DEFAULT (30)';
EXEC dbo.Feat_AddColumn 'OtherDocumentMaster', 'CertificateNo', 'nvarchar(50) NULL';
EXEC dbo.Feat_AddColumn 'OtherDocumentMaster', 'IssuedBy',      'nvarchar(150) NULL';
EXEC dbo.Feat_AddColumn 'OtherDocumentMaster', 'IssueDate',     'datetime NULL';
EXEC dbo.Feat_AddColumn 'OtherDocumentMaster', 'ExpiryDate',    'datetime NULL';
EXEC dbo.Feat_AddColumn 'OtherDocumentMaster', 'NextSurveyDate','datetime NULL';
EXEC dbo.Feat_AddColumn 'OtherDocumentMaster', 'ReplacesID',    'bigint NULL';
GO

/* ------------------------------------------------------------------------ New tables ---- */

IF OBJECT_ID('dbo.DMS_Inbox', 'U') IS NULL
CREATE TABLE dbo.DMS_Inbox
(
    DMS_InboxID   bigint        NOT NULL CONSTRAINT PK_DMS_Inbox PRIMARY KEY,
    Origin        varchar(3)    NOT NULL,
    VesselID      int           NOT NULL,          -- -1 all vessels and the office, a vessel, -2 office only
    ItemType      varchar(20)   NOT NULL,          -- Manual | Circular | Alert | Document | Certificate | Suggestion
    ItemID        bigint        NOT NULL,
    VersionID     bigint        NULL,
    Action        varchar(20)   NOT NULL,          -- New | Updated | Cancelled | Expiring | Expired | <suggestion status>
    Title         nvarchar(500) NULL,
    Priority      int           NULL,
    TargetUserID  varchar(50)   NULL,              -- NULL = everyone the item is for
    TargetRank    varchar(50)   NULL,
    CreatedOn     datetime      NOT NULL,
    IsActive      int           NOT NULL CONSTRAINT DF_DMS_Inbox_IsActive DEFAULT (1)
);
IF OBJECT_ID('dbo.DMS_InboxRead', 'U') IS NULL
CREATE TABLE dbo.DMS_InboxRead
(
    DMS_InboxReadID bigint      NOT NULL CONSTRAINT PK_DMS_InboxRead PRIMARY KEY,
    Origin          varchar(3)  NOT NULL,
    VesselID        int         NOT NULL,
    DMS_InboxID     bigint      NOT NULL,
    UserID          varchar(50) NOT NULL,
    ReadOn          datetime    NOT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DMS_InboxRead_User')
    CREATE INDEX IX_DMS_InboxRead_User ON dbo.DMS_InboxRead (UserID, DMS_InboxID);

IF OBJECT_ID('dbo.CIR_ManualLink', 'U') IS NULL
CREATE TABLE dbo.CIR_ManualLink
(
    CIR_ManualLinkID   bigint        NOT NULL CONSTRAINT PK_CIR_ManualLink PRIMARY KEY,
    Origin             varchar(3)    NOT NULL,
    VesselID           int           NOT NULL,
    CIR_MasterID       bigint        NOT NULL,
    DM_ManualID        bigint        NOT NULL,
    DM_ManualVersionID bigint        NULL,
    LinkType           varchar(20)   NOT NULL,     -- Amends | Supersedes | RefersTo
    Remarks            nvarchar(500) NULL,
    IsActive           int           NOT NULL CONSTRAINT DF_CIR_ManualLink_IsActive DEFAULT (1),
    ClosedOn           datetime      NULL,
    CreatedBy          varchar(50)   NULL,
    CreatedOn          datetime      NULL
);

IF OBJECT_ID('dbo.SUG_Master', 'U') IS NULL
CREATE TABLE dbo.SUG_Master
(
    SUG_MasterID        bigint        NOT NULL CONSTRAINT PK_SUG_Master PRIMARY KEY,
    Origin              varchar(3)    NOT NULL,
    VesselID            int           NOT NULL,    -- the vessel it came from, or -2 when submitted at the office
    SuggestionNo        varchar(30)   NULL,
    Title               nvarchar(250) NOT NULL,
    Description         nvarchar(max) NULL,
    ItemType            varchar(20)   NULL,        -- Manual | Circular | Alert | Document | NULL (general)
    ItemID              bigint        NULL,
    QuotedText          nvarchar(1000) NULL,
    Priority            varchar(10)   NOT NULL CONSTRAINT DF_SUG_Master_Priority DEFAULT ('Medium'),
    Status              varchar(20)   NOT NULL CONSTRAINT DF_SUG_Master_Status DEFAULT ('Submitted'),
    SubmittedBy         varchar(50)   NOT NULL,
    SubmittedByName     nvarchar(250) NULL,
    SubmittedRank       varchar(50)   NULL,
    SubmittedOn         datetime      NOT NULL,
    AssignedTo          varchar(50)   NULL,
    ImplementedItemType varchar(20)   NULL,
    ImplementedItemID   bigint        NULL,
    ModifiedOn          datetime      NULL,
    IsDeleted           int           NOT NULL CONSTRAINT DF_SUG_Master_IsDeleted DEFAULT (0)
);
IF OBJECT_ID('dbo.SUG_Log', 'U') IS NULL
CREATE TABLE dbo.SUG_Log
(
    SUG_LogID     bigint         NOT NULL CONSTRAINT PK_SUG_Log PRIMARY KEY,
    Origin        varchar(3)     NOT NULL,
    VesselID      int            NOT NULL,
    SUG_MasterID  bigint         NOT NULL,
    Status        varchar(20)    NULL,
    Comment       nvarchar(2000) NULL,
    UserID        varchar(50)    NULL,
    UserName      nvarchar(250)  NULL,
    LogDate       datetime       NOT NULL
);
IF OBJECT_ID('dbo.SUG_Blob', 'U') IS NULL
CREATE TABLE dbo.SUG_Blob
(
    SUG_BlobID    bigint         NOT NULL CONSTRAINT PK_SUG_Blob PRIMARY KEY,
    Origin        varchar(3)     NOT NULL,
    VesselID      int            NOT NULL,
    SUG_MasterID  bigint         NOT NULL,
    FileName      nvarchar(250)  NULL,
    FileType      varchar(50)    NULL,
    BlobSize      int            NOT NULL,
    BlobContents  varbinary(max) NULL,
    AddedOn       datetime       NULL
);

-- Renamed to dbo.OtherDocument_Folder (key OtherDocument_FolderID) by 022_RenameFolderTable.sql.
IF OBJECT_ID('dbo.FLD_Folder', 'U') IS NULL AND OBJECT_ID('dbo.OtherDocument_Folder', 'U') IS NULL
CREATE TABLE dbo.FLD_Folder
(
    FLD_FolderID  bigint         NOT NULL CONSTRAINT PK_FLD_Folder PRIMARY KEY,
    Origin        varchar(3)     NOT NULL,
    VesselID      int            NOT NULL,    -- Company: -1 or a vessel; Vessel: the vessel; Department / Personal: -2 at the office
    ParentID      bigint         NULL,
    FolderName    nvarchar(150)  NOT NULL,
    Scope         varchar(20)    NOT NULL,    -- Company | Vessel | Department | Personal
    OwnerUserID   varchar(50)    NULL,        -- Personal: the owner; Department: the department
    ViewRoles     nvarchar(500)  NULL,        -- comma-separated roles / ranks that may open it; NULL = everyone in the scope
    EditRoles     nvarchar(500)  NULL,        -- comma-separated roles / ranks that may change it; NULL = owner / office only
    SortOrder     int            NULL,
    CreatedBy     varchar(50)    NULL,
    CreatedOn     datetime       NULL,
    IsDeleted     int            NOT NULL CONSTRAINT DF_FLD_Folder_IsDeleted DEFAULT (0)
);
GO

EXEC dbo.Feat_AddSetting 'DM_Settings', 'FLD_MaxFileSizeMB', '25';
EXEC dbo.Feat_AddSetting 'DM_Settings', 'FLD_MaxFiles', '1000';
GO

/* --------------------------------------------------------------- 2. Inbox entries (triggers) ---- */

/* One inbox entry. ID from Proc_NextID (office range). */
CREATE OR ALTER PROCEDURE dbo.Inbox_Add
    @VesselID int, @ItemType varchar(20), @ItemID bigint, @VersionID bigint, @Action varchar(20),
    @Title nvarchar(500), @Priority int, @TargetUserID varchar(50) = NULL, @TargetRank varchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @id bigint;
    EXEC dbo.Proc_NextID @TableName = 'DMS_Inbox', @Origin = 'HQ', @VesselID = -1, @Incrementor = 1, @NewTableID = @id OUTPUT;
    INSERT dbo.DMS_Inbox (DMS_InboxID, Origin, VesselID, ItemType, ItemID, VersionID, Action, Title, Priority, TargetUserID, TargetRank, CreatedOn)
    VALUES (@id, 'HQ', @VesselID, @ItemType, @ItemID, @VersionID, @Action, @Title, @Priority, @TargetUserID, @TargetRank, GETUTCDATE());
END
GO

/* A manual version released (ReleasedDate set): New for the first version of a manual, Updated after that. */
CREATE OR ALTER TRIGGER dbo.TR_DM_ManualVersion_Inbox ON dbo.DM_ManualVersion AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ManualID bigint, VersionID bigint, Title nvarchar(500), IsFirst bit);
    INSERT @rows
    SELECT i.DM_ManualID, i.DM_ManualVersionID,
           LEFT(LTRIM(ISNULL(m.ManualNo, '') + ' ' + ISNULL(m.ManualName, '')) + ' (v' + ISNULL(i.Version, '') + ')', 500),
           CASE WHEN EXISTS (SELECT 1 FROM dbo.DM_ManualVersion o WHERE o.DM_ManualID = i.DM_ManualID
                                AND o.DM_ManualVersionID <> i.DM_ManualVersionID AND o.ReleasedDate IS NOT NULL) THEN 0 ELSE 1 END
      FROM inserted i
      LEFT JOIN deleted d ON d.DM_ManualVersionID = i.DM_ManualVersionID
      JOIN dbo.DM_Manual m ON m.DM_ManualID = i.DM_ManualID
     WHERE i.ReleasedDate IS NOT NULL AND d.ReleasedDate IS NULL AND ISNULL(m.IsDeleted, 0) <> 1;
    DECLARE @mid bigint, @vid bigint, @title nvarchar(500), @first bit, @action varchar(20);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ManualID, VersionID, Title, IsFirst FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @mid, @vid, @title, @first;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @action = CASE WHEN @first = 1 THEN 'New' ELSE 'Updated' END;
        EXEC dbo.Inbox_Add -1, 'Manual', @mid, @vid, @action, @title, NULL;
        FETCH NEXT FROM c INTO @mid, @vid, @title, @first;
    END
    CLOSE c; DEALLOCATE c;
END
GO

/* A circular / alert released (leaves Temp / Draft), or cancelled. */
CREATE OR ALTER TRIGGER dbo.TR_CIR_Master_Inbox ON dbo.CIR_Master AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ID bigint, ItemType varchar(20), Action varchar(20), Title nvarchar(500), Priority int);
    INSERT @rows
    SELECT i.CIR_MasterID,
           CASE WHEN i.CircularType LIKE 'Alert%' THEN 'Alert' ELSE 'Circular' END,
           CASE WHEN i.Status = 'Cancelled' THEN 'Cancelled' ELSE 'New' END,
           LEFT(LTRIM(ISNULL(i.CIR_Number, '') + ' ' + ISNULL(i.Title, '')), 500), i.Priority
      FROM inserted i
      LEFT JOIN deleted d ON d.CIR_MasterID = i.CIR_MasterID
     WHERE ISNULL(i.IsDeleted, 0) <> 1
       AND (   (ISNULL(i.Status, '') NOT IN ('Temp', 'Draft', 'Cancelled') AND (d.CIR_MasterID IS NULL OR ISNULL(d.Status, '') IN ('Temp', 'Draft')))
            OR (i.Status = 'Cancelled' AND ISNULL(d.Status, '') <> 'Cancelled'));
    DECLARE @id bigint, @type varchar(20), @action varchar(20), @title nvarchar(500), @priority int;
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ID, ItemType, Action, Title, Priority FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @id, @type, @action, @title, @priority;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.Inbox_Add -1, @type, @id, NULL, @action, @title, @priority;
        FETCH NEXT FROM c INTO @id, @type, @action, @title, @priority;
    END
    CLOSE c; DEALLOCATE c;
END
GO

/* A document added (at the office, or received from a vessel). */
CREATE OR ALTER TRIGGER dbo.TR_OtherDocumentMaster_Inbox ON dbo.OtherDocumentMaster AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ID bigint, VesselID int, ItemType varchar(20), Title nvarchar(500));
    INSERT @rows
    SELECT i.OtherDocumentMasterID, i.VesselID,
           'Document',
           LEFT(LTRIM(ISNULL(i.DocumentNumber, '') + ' ' + ISNULL(i.DocumentTitle, '')), 500)
      FROM inserted i
      LEFT JOIN dbo.OtherDocument_Type t ON t.OtherDocument_TypeID = i.OtherDocument_TypeID
     WHERE ISNULL(i.IsDeleted, 0) <> 1 AND ISNULL(i.DocumentTitle, '') <> '';
    DECLARE @id bigint, @vessel int, @type varchar(20), @title nvarchar(500);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ID, VesselID, ItemType, Title FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @id, @vessel, @type, @title;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.Inbox_Add @vessel, @type, @id, NULL, 'New', @title, NULL;
        FETCH NEXT FROM c INTO @id, @vessel, @type, @title;
    END
    CLOSE c; DEALLOCATE c;
END
GO

/* Suggestions: a new one goes to the office (its reviewer, or every office user); a status change goes to the submitter. */
CREATE OR ALTER TRIGGER dbo.TR_SUG_Master_Inbox ON dbo.SUG_Master AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ID bigint, VesselID int, Action varchar(20), Title nvarchar(500), TargetUser varchar(50));
    INSERT @rows
    SELECT i.SUG_MasterID,
           CASE WHEN d.SUG_MasterID IS NULL THEN -2 ELSE i.VesselID END,
           CASE WHEN d.SUG_MasterID IS NULL THEN 'New' ELSE i.Status END,
           LEFT(ISNULL(i.SuggestionNo + ' ', '') + i.Title, 500),
           CASE WHEN d.SUG_MasterID IS NULL THEN i.AssignedTo ELSE i.SubmittedBy END
      FROM inserted i
      LEFT JOIN deleted d ON d.SUG_MasterID = i.SUG_MasterID
     WHERE i.IsDeleted = 0 AND (d.SUG_MasterID IS NULL OR d.Status <> i.Status);
    DECLARE @id bigint, @vessel int, @action varchar(20), @title nvarchar(500), @user varchar(50);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ID, VesselID, Action, Title, TargetUser FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @id, @vessel, @action, @title, @user;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.Inbox_Add @vessel, 'Suggestion', @id, NULL, @action, @title, NULL, @user;
        FETCH NEXT FROM c INTO @id, @vessel, @action, @title, @user;
    END
    CLOSE c; DEALLOCATE c;
END
GO

/* Certificates entering their warning window or expiring: one inbox entry each time (run by the office API). */
CREATE OR ALTER PROCEDURE dbo.Inbox_CertificateExpiry
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ID bigint, VesselID int, Action varchar(20), Title nvarchar(500));
    INSERT @rows
    SELECT m.OtherDocumentMasterID, m.VesselID,
           CASE WHEN m.ExpiryDate < CAST(GETDATE() AS date) THEN 'Expired' ELSE 'Expiring' END,
           LEFT(LTRIM(ISNULL(m.DocumentNumber, '') + ' ' + ISNULL(m.DocumentTitle, '')), 500)
      FROM dbo.OtherDocumentMaster m
      JOIN dbo.OtherDocument_Type t ON t.OtherDocument_TypeID = m.OtherDocument_TypeID AND t.IsCertificate = 1
     WHERE ISNULL(m.IsDeleted, 0) <> 1 AND m.ExpiryDate IS NOT NULL
       AND m.ExpiryDate < DATEADD(day, t.WarningDays, CAST(GETDATE() AS date))
       AND NOT EXISTS (SELECT 1 FROM dbo.OtherDocumentMaster n WHERE n.ReplacesID = m.OtherDocumentMasterID AND ISNULL(n.IsDeleted, 0) <> 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.DMS_Inbox x WHERE x.ItemType = 'Certificate' AND x.ItemID = m.OtherDocumentMasterID
                         AND x.Action = CASE WHEN m.ExpiryDate < CAST(GETDATE() AS date) THEN 'Expired' ELSE 'Expiring' END);
    DECLARE @id bigint, @vessel int, @action varchar(20), @title nvarchar(500);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ID, VesselID, Action, Title FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @id, @vessel, @action, @title;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.Inbox_Add @vessel, 'Certificate', @id, NULL, @action, @title, NULL;
        FETCH NEXT FROM c INTO @id, @vessel, @action, @title;
    END
    CLOSE c; DEALLOCATE c;
END
GO

/* ------------------------------------------------------- Sync: -2 = office only (never sent) ---- */

CREATE OR ALTER PROCEDURE dbo.Sync_LogChanges
    @TableName varchar(128),
    @Action    char(1),
    @Rows      dbo.Sync_RowList READONLY
AS
BEGIN
    SET NOCOUNT ON;
    -- VesselID: a vessel = that vessel; -1, 0 or NULL = every active vessel; -2 (or any other negative) = nobody.
    ;WITH expanded AS
    (
        SELECT DISTINCT x.VesselID, r.KeyValue
        FROM @Rows r
        CROSS APPLY (
            SELECT r.VesselID AS VesselID WHERE r.VesselID > 0
            UNION
            SELECT v.VesselID FROM dbo.V_Vessel v
             WHERE (r.VesselID IS NULL OR r.VesselID IN (-1, 0)) AND v.VesselID > 0 AND ISNULL(v.IsRetired, 0) = 0
        ) x
    )
    INSERT dbo.Sync_ChangeLog (VesselID, TableName, KeyValue, Action)
    SELECT e.VesselID, @TableName, e.KeyValue, @Action
      FROM expanded e
     WHERE NOT EXISTS (SELECT 1 FROM dbo.Sync_ChangeLog w
                        WHERE w.VesselID = e.VesselID AND w.TableName = @TableName AND w.KeyValue = e.KeyValue
                          AND w.SentUtc IS NULL AND w.DownloadedUtc IS NULL);
END
GO

CREATE OR ALTER PROCEDURE dbo.Sync_QueueFullCopy
    @VesselID int = -1
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @table varchar(128), @all bit, @key sysname, @sql nvarchar(max), @vessels nvarchar(400);
    DECLARE t CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableName, AllVessels FROM dbo.Sync_Table WHERE IsActive = 1 ORDER BY SortOrder;
    OPEN t; FETCH NEXT FROM t INTO @table, @all;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @key = dbo.Sync_KeyColumn(@table);
        IF OBJECT_ID(N'dbo.' + QUOTENAME(@table), N'U') IS NOT NULL AND @key IS NOT NULL
        BEGIN
            SET @vessels = CASE WHEN @all = 0 AND COL_LENGTH(N'dbo.' + QUOTENAME(@table), 'VesselID') IS NOT NULL
                                THEN N'r.VesselID' ELSE N'-1' END;
            SET @sql = N'
INSERT dbo.Sync_ChangeLog (VesselID, TableName, KeyValue, Action)
SELECT v.VesselID, @table, CAST(r.' + QUOTENAME(@key) + N' AS bigint), ''U''
  FROM dbo.' + QUOTENAME(@table) + N' r
  JOIN dbo.V_Vessel v ON v.VesselID > 0 AND ISNULL(v.IsRetired, 0) = 0
                     AND (@VesselID = -1 OR v.VesselID = @VesselID)
                     AND (' + @vessels + N' = v.VesselID OR ISNULL(' + @vessels + N', -1) IN (-1, 0))
 WHERE NOT EXISTS (SELECT 1 FROM dbo.Sync_ChangeLog w
                    WHERE w.VesselID = v.VesselID AND w.TableName = @table AND w.KeyValue = CAST(r.' + QUOTENAME(@key) + N' AS bigint)
                      AND w.SentUtc IS NULL AND w.DownloadedUtc IS NULL);';
            EXEC sp_executesql @sql, N'@table varchar(128), @VesselID int', @table = @table, @VesselID = @VesselID;
            PRINT @table + ': ' + CAST(@@ROWCOUNT AS varchar(20)) + ' row(s) queued.';
        END
        FETCH NEXT FROM t INTO @table, @all;
    END
    CLOSE t; DEALLOCATE t;
END
GO

/* ------------------------------------------------- Which new tables sync, and which ships hold ---- */

-- Office -> vessels (row-by-row sync). DMS_InboxRead is not sent down: read receipts only go up.
MERGE dbo.Sync_Table AS t
USING (VALUES
    ('CIR_ManualLink', 250, NULL,           0),
    ('DMS_Inbox',      400, NULL,           0),
    ('SUG_Master',     500, NULL,           0),
    ('SUG_Log',        510, NULL,           0),
    ('SUG_Blob',       520, 'BlobContents', 0),
    ('FLD_Folder',     600, NULL,           0)
) AS s (TableName, SortOrder, BlobColumn, AllVessels)
ON t.TableName = s.TableName
WHEN NOT MATCHED THEN INSERT (TableName, SortOrder, BlobColumn, AllVessels) VALUES (s.TableName, s.SortOrder, s.BlobColumn, s.AllVessels);
-- Run again after 022_RenameFolderTable.sql: the folders are synced as OtherDocument_Folder.
IF OBJECT_ID('dbo.OtherDocument_Folder', 'U') IS NOT NULL
    DELETE FROM dbo.Sync_Table WHERE TableName = 'FLD_Folder';
GO
EXEC dbo.Sync_CreateRowTriggers;
GO

-- Tables a new vessel database gets (Vsl_CreateVesselDatabase copies TableID_Setup rows with IsForVsl = 1).
IF COL_LENGTH('dbo.TableID_Setup', 'IsForVsl') IS NOT NULL
BEGIN
    DECLARE @t TABLE (TableName varchar(50));
    INSERT @t VALUES ('CIR_ManualLink'), ('DMS_Inbox'), ('DMS_InboxRead'), ('SUG_Master'), ('SUG_Log'), ('SUG_Blob'),
                     ('FLD_Folder');
    DECLARE @name varchar(50), @id bigint;
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @t;
    OPEN c; FETCH NEXT FROM c INTO @name;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.TableID_Setup WHERE TableName = @name)
            EXEC sp_executesql N'UPDATE dbo.TableID_Setup SET IsForVsl = 1 WHERE TableName = @n', N'@n varchar(50)', @n = @name;
        ELSE
        BEGIN
            SELECT @id = ISNULL(MAX(TableID_SetupID), 0) + 1 FROM dbo.TableID_Setup;
            EXEC sp_executesql N'INSERT dbo.TableID_Setup (TableID_SetupID, TableName, VesselID, LastNumber, IsForVsl)
                                 VALUES (@id, @n, -1, 0, 1)', N'@id bigint, @n varchar(50)', @id = @id, @n = @name;
        END
        FETCH NEXT FROM c INTO @name;
    END
    CLOSE c; DEALLOCATE c;
END
GO

IF OBJECT_ID('dbo.OtherDocument_Folder', 'U') IS NOT NULL AND OBJECT_ID('dbo.TableID_Setup', 'U') IS NOT NULL
    DELETE FROM dbo.TableID_Setup WHERE TableName = 'FLD_Folder';   -- renamed by 022_RenameFolderTable.sql
GO

PRINT 'New features are ready in the office database.';
GO
