/* =====================================================================================================
   010_Sync_ChangeLog.sql  -  run on the OFFICE (HQ) database. Safe to run again.

   Row-by-row vessel sync. Replaces the item-based sync of 006_VesselSyncOutbox.sql (its triggers are dropped
   here; the old api/Sync/Pull keeps working for ships that are not updated yet, by comparing contents).

   1. Capture  -  every insert, update and delete in a synced table (dbo.Sync_Table) writes, per vessel, a row into
                  dbo.Sync_ChangeLog with the table name, the record's key and the action (I / U / D):
                    - record VesselID = a vessel     -> one row for that vessel
                    - record VesselID = -1 (or 0, or the table has no VesselID / AllVessels = 1)
                                                     -> one row for every vessel in V_Vessel that is not retired
                  A record that changes vessel (VesselID X -> Y) is logged for both, so X removes it.
   2. Sync     -  the vessel's next call (api/Sync/Changes) reads its waiting rows, and for each one the record as it
                  is now, by its key: all columns, the same key, nothing changed (file contents excepted - see
                  BlobColumn). A record that no longer exists, or no longer belongs to that vessel, goes as a delete.
                  The ship writes the record into its own table of the same name with the same key and data.
   3. Confirm  -  once the ship has saved them it confirms the rows (api/Sync/ChangesConfirm): DownloadedUtc is set.
                  A row sent but not confirmed (lost answer) is simply sent again.

   After running this script for the first time, queue a full copy for each vessel so its ship database starts
   complete:   EXEC dbo.Sync_QueueFullCopy @VesselID = -1;      -- -1 = every active vessel
   ===================================================================================================== */

SET NOCOUNT ON;
GO

/* ---------------------------------------------------------------------------------- Tables ---- */

IF OBJECT_ID('dbo.Sync_Table', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Sync_Table
    (
        TableName   varchar(128) NOT NULL CONSTRAINT PK_Sync_Table PRIMARY KEY,
        SortOrder   int          NOT NULL,             -- order the ship writes them in (parents before children)
        BlobColumn  varchar(128) NULL,                 -- file contents: not sent, the ship downloads them when opened
        AllVessels  bit          NOT NULL CONSTRAINT DF_Sync_Table_AllVessels DEFAULT (0), -- ignore VesselID: every vessel
        IsActive    bit          NOT NULL CONSTRAINT DF_Sync_Table_IsActive DEFAULT (1)
    );
END
GO

IF OBJECT_ID('dbo.Sync_ChangeLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Sync_ChangeLog
    (
        SyncChangeID   bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sync_ChangeLog PRIMARY KEY,
        VesselID       int           NOT NULL,
        TableName      varchar(128)  NOT NULL,
        KeyValue       bigint        NOT NULL,
        Action         char(1)       NOT NULL,     -- I | U | D
        CreatedUtc     datetime2(3)  NOT NULL CONSTRAINT DF_Sync_ChangeLog_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        SentUtc        datetime2(3)  NULL,         -- went out in an api/Sync/Changes answer
        DownloadedUtc  datetime2(3)  NULL          -- the ship confirmed it has saved it
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Sync_ChangeLog_Waiting' AND object_id = OBJECT_ID('dbo.Sync_ChangeLog'))
    CREATE INDEX IX_Sync_ChangeLog_Waiting ON dbo.Sync_ChangeLog (VesselID, DownloadedUtc, SyncChangeID)
        INCLUDE (TableName, KeyValue, Action, SentUtc);
GO

/* The tables the ships hold. Their key (single-column primary key) and VesselID column are read from the table. */
MERGE dbo.Sync_Table AS t
USING (VALUES
    ('V_Vessel',               10, NULL,           1),
    ('DM_Status',              20, NULL,           0),
    ('DM_Settings',            30, NULL,           0),
    ('CIR_Settings',            40, NULL,           0),
    ('Vsl_Staff',              50, NULL,           0),
    ('DM_Category',           100, NULL,           0),
    ('DM_Manual',             110, NULL,           0),
    ('DM_ManualVersion',      120, NULL,           0),
    ('DM_Manual_Attachments', 130, 'BlobContents', 0),
    ('DM_Manual_ToAck_Vsl',   140, NULL,           0),
    ('DM_Manual_UserAck',     150, NULL,           0),
    ('CIR_Category',          200, NULL,           0),
    ('CIR_Master',            210, NULL,           0),
    ('CIR_Blob',              220, 'BlobContents', 0),
    ('CIR_ToAck_Vsl',         230, NULL,           0),
    ('CIR_UserAck',           240, NULL,           0),
    ('OtherDocument_Type',    300, NULL,           0),
    ('OtherDocumentMaster',   310, NULL,           0),
    ('OtherDocument_Blob',    320, 'BlobContents', 0)
) AS s (TableName, SortOrder, BlobColumn, AllVessels)
ON t.TableName = s.TableName
WHEN NOT MATCHED THEN INSERT (TableName, SortOrder, BlobColumn, AllVessels) VALUES (s.TableName, s.SortOrder, s.BlobColumn, s.AllVessels);
GO

/* ------------------------------------------------------------------- The old item triggers ---- */

DECLARE @old TABLE (Name sysname);
INSERT @old VALUES ('TR_DM_Manual_Sync'), ('TR_DM_ManualVersion_Sync'), ('TR_DM_Manual_Attachments_Sync'),
                   ('TR_DM_Manual_ToAck_Vsl_Sync'), ('TR_CIR_Master_Sync'), ('TR_CIR_Blob_Sync'), ('TR_CIR_ToAck_Vsl_Sync'),
                   ('TR_Vsl_Staff_Sync'), ('TR_DM_Category_Sync'), ('TR_CIR_Category_Sync');
DECLARE @name sysname, @sql nvarchar(max);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @old WHERE OBJECT_ID('dbo.' + Name, 'TR') IS NOT NULL;
OPEN c; FETCH NEXT FROM c INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'DROP TRIGGER dbo.' + QUOTENAME(@name) + N';';
    EXEC sp_executesql @sql;
    FETCH NEXT FROM c INTO @name;
END
CLOSE c; DEALLOCATE c;
GO

/* ------------------------------------------------------------- Key and vessel column lookup ---- */

/* The single-column primary key of a table, and whether it has a VesselID column. */
CREATE OR ALTER FUNCTION dbo.Sync_KeyColumn (@TableName sysname)
RETURNS sysname
AS
BEGIN
    DECLARE @key sysname;
    SELECT @key = c.name
      FROM sys.indexes i
      JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
      JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
     WHERE i.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U') AND i.is_primary_key = 1
       AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 1;
    IF @key IS NULL AND COL_LENGTH(N'dbo.' + QUOTENAME(@TableName), @TableName + 'ID') IS NOT NULL
        SET @key = @TableName + 'ID';
    RETURN @key;
END
GO

/* ------------------------------------------------------------------------ Writing the rows ---- */

IF TYPE_ID('dbo.Sync_RowList') IS NULL
    CREATE TYPE dbo.Sync_RowList AS TABLE
    (
        KeyValue  bigint  NOT NULL,
        VesselID  int     NULL       -- NULL or <= 0 = every vessel
    );
GO

/* One Sync_ChangeLog row per vessel for each changed record: VesselID -1 (0, NULL) is every active vessel.
   A record that already has a row waiting (not sent yet) for that vessel gets no second one: the ship always
   receives the record as it is when it syncs. */
CREATE OR ALTER PROCEDURE dbo.Sync_LogChanges
    @TableName varchar(128),
    @Action    char(1),
    @Rows      dbo.Sync_RowList READONLY
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH expanded AS
    (
        SELECT DISTINCT x.VesselID, r.KeyValue
        FROM @Rows r
        CROSS APPLY (
            SELECT r.VesselID AS VesselID WHERE r.VesselID > 0
            UNION
            SELECT v.VesselID FROM dbo.V_Vessel v
             WHERE (r.VesselID IS NULL OR r.VesselID <= 0) AND v.VesselID > 0 AND ISNULL(v.IsRetired, 0) = 0
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

/* ------------------------------------------------------------- One trigger per synced table ---- */

/* Creates (or re-creates) TR_<table>_SyncRow on every active table in Sync_Table. Run again after adding a table. */
CREATE OR ALTER PROCEDURE dbo.Sync_CreateRowTriggers
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @table varchar(128), @all bit, @key sysname, @vessel nvarchar(200), @sql nvarchar(max), @trigger sysname;

    DECLARE t CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableName, AllVessels FROM dbo.Sync_Table WHERE IsActive = 1 ORDER BY SortOrder;
    OPEN t; FETCH NEXT FROM t INTO @table, @all;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @trigger = N'TR_' + @table + N'_SyncRow';
        IF OBJECT_ID(N'dbo.' + QUOTENAME(@table), N'U') IS NULL
            PRINT 'Skipped ' + @table + ': no such table in this database.';
        ELSE
        BEGIN
            SET @key = dbo.Sync_KeyColumn(@table);
            IF @key IS NULL
                PRINT 'Skipped ' + @table + ': no single-column primary key.';
            ELSE
            BEGIN
                SET @vessel = CASE WHEN @all = 0 AND COL_LENGTH(N'dbo.' + QUOTENAME(@table), 'VesselID') IS NOT NULL
                                   THEN N'CAST(VesselID AS int)' ELSE N'CAST(NULL AS int)' END;
                SET @sql = N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(@trigger) + N' ON dbo.' + QUOTENAME(@table) + N'
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    DECLARE @act char(1) = CASE WHEN EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) THEN ''U''
                                WHEN EXISTS (SELECT 1 FROM inserted) THEN ''I'' ELSE ''D'' END;
    DECLARE @rows dbo.Sync_RowList;
    INSERT @rows (KeyValue, VesselID)
    SELECT CAST(' + QUOTENAME(@key) + N' AS bigint), ' + @vessel + N' FROM inserted
    UNION
    SELECT CAST(' + QUOTENAME(@key) + N' AS bigint), ' + @vessel + N' FROM deleted;
    EXEC dbo.Sync_LogChanges @TableName = ''' + @table + N''', @Action = @act, @Rows = @rows;
END;';
                EXEC sp_executesql @sql;
                PRINT 'Trigger ' + @trigger + ' ready (key ' + @key + ').';
            END
        END
        FETCH NEXT FROM t INTO @table, @all;
    END
    CLOSE t; DEALLOCATE t;
END
GO

EXEC dbo.Sync_CreateRowTriggers;
GO

/* --------------------------------------------------------------------------- Full copy ---- */

/* Queues every record of every synced table that belongs to the vessel (its VesselID, or -1 / all vessels), so
   the vessel's next syncs bring its ship database complete. @VesselID -1 = every active vessel. Safe to repeat:
   a record already waiting is not queued twice, and the ship writes each record by its key. */
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
            -- The vessels each record goes to: its own vessel, or every active vessel for -1 / AllVessels.
            SET @vessels = CASE WHEN @all = 0 AND COL_LENGTH(N'dbo.' + QUOTENAME(@table), 'VesselID') IS NOT NULL
                                THEN N'r.VesselID' ELSE N'-1' END;
            SET @sql = N'
INSERT dbo.Sync_ChangeLog (VesselID, TableName, KeyValue, Action)
SELECT v.VesselID, @table, CAST(r.' + QUOTENAME(@key) + N' AS bigint), ''U''
  FROM dbo.' + QUOTENAME(@table) + N' r
  JOIN dbo.V_Vessel v ON v.VesselID > 0 AND ISNULL(v.IsRetired, 0) = 0
                     AND (@VesselID = -1 OR v.VesselID = @VesselID)
                     AND (' + @vessels + N' = v.VesselID OR ISNULL(' + @vessels + N', -1) <= 0)
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

PRINT 'Row-by-row sync is ready. For a first run: EXEC dbo.Sync_QueueFullCopy @VesselID = -1;';
GO
