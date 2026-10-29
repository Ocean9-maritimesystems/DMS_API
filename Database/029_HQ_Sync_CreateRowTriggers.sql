/* =====================================================================================================
   029_HQ_Sync_CreateRowTriggers.sql  -  run on the OFFICE (HQ) database. Creates (or replaces) dbo.Sync_CreateRowTriggers.

   The procedure makes the office row-sync triggers from dbo.TableID_Setup:
       HQ_Has_InsDEL_Trigger = 1   ->  TR_<table>_SyncRow_InsDel   AFTER INSERT, DELETE   (Sync_ChangeLog: I / D)
       HQ_Has_Upd_Trigger    = 1   ->  TR_<table>_SyncRow_Upd      AFTER UPDATE           (Sync_ChangeLog: U)
   A flag at 0 removes that trigger; the older single trigger TR_<table>_SyncRow is replaced. A table active in
   Sync_Table with no row in TableID_Setup keeps both triggers.

   Usage:   EXEC dbo.Sync_CreateRowTriggers;                          -- every table
            EXEC dbo.Sync_CreateRowTriggers @TableName = 'CIR_Master'; -- one table
   ===================================================================================================== */
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.Sync_CreateRowTriggers
    @TableName sysname = NULL       -- one table, or NULL = every table in TableID_Setup
AS
BEGIN
    /* The office row-sync triggers, as dbo.TableID_Setup says per table:
         HQ_Has_InsDEL_Trigger = 1  ->  TR_<table>_SyncRow_InsDel  AFTER INSERT, DELETE   (Sync_ChangeLog: I / D)
         HQ_Has_Upd_Trigger    = 1  ->  TR_<table>_SyncRow_Upd     AFTER UPDATE           (Sync_ChangeLog: U)
       A flag at 0 removes that trigger; the older single trigger TR_<table>_SyncRow is replaced by these two.
       Each trigger hands the changed keys to dbo.Sync_LogChanges, which writes one Sync_ChangeLog row per vessel that
       should get the record (its VesselID; -1, 0 or no VesselID column = every active vessel; Sync_Table.AllVessels = 1
       = every vessel whatever VesselID says). A record moved from one vessel to another is logged for both.
       Only tables listed (active) in Sync_Table are sent to the ships; the result says where flags and Sync_Table differ.
       If TableID_Setup has no HQ flag columns, every active table in Sync_Table gets both triggers.
       Office database only. */
    SET NOCOUNT ON;

    IF OBJECT_ID(N'dbo.Sync_ChangeLog', N'U') IS NULL OR OBJECT_ID(N'dbo.Sync_VesselKey', N'U') IS NULL
       OR OBJECT_ID(N'dbo.Sync_LogChanges', N'P') IS NULL OR TYPE_ID(N'dbo.Sync_RowList') IS NULL
    BEGIN
        RAISERROR('Sync_CreateRowTriggers: this is not the office database with the row sync (Sync_ChangeLog, Sync_VesselKey, Sync_LogChanges, Sync_RowList).', 16, 1);
        RETURN;
    END;

    DECLARE @flags TABLE (TableName sysname COLLATE DATABASE_DEFAULT PRIMARY KEY, InsDel bit NOT NULL, Upd bit NOT NULL,
                          NoSetupRow bit NOT NULL DEFAULT (0));
    DECLARE @source varchar(20);

    IF OBJECT_ID(N'dbo.TableID_Setup', N'U') IS NOT NULL
       AND COL_LENGTH('dbo.TableID_Setup', 'HQ_Has_InsDEL_Trigger') IS NOT NULL
       AND COL_LENGTH('dbo.TableID_Setup', 'HQ_Has_Upd_Trigger') IS NOT NULL
    BEGIN
        SET @source = 'TableID_Setup';
        -- A table can have several rows (per origin / vessel): a flag set on any of them counts.
        INSERT @flags (TableName, InsDel, Upd)
        EXEC sp_executesql N'
            SELECT LTRIM(RTRIM(TableName)),
                   MAX(CASE WHEN ISNULL(HQ_Has_InsDEL_Trigger, 0) = 1 THEN 1 ELSE 0 END),
                   MAX(CASE WHEN ISNULL(HQ_Has_Upd_Trigger, 0) = 1 THEN 1 ELSE 0 END)
              FROM dbo.TableID_Setup
             WHERE NULLIF(LTRIM(RTRIM(TableName)), '''') IS NOT NULL
               AND (@TableName IS NULL OR LTRIM(RTRIM(TableName)) = @TableName)
             GROUP BY LTRIM(RTRIM(TableName));', N'@TableName sysname', @TableName = @TableName;

        -- A table the ships get that has no TableID_Setup row at all (V_Vessel, DM_Status, ...) keeps both triggers,
        -- so it does not stop syncing; give it a row to set its flags.
        INSERT @flags (TableName, InsDel, Upd, NoSetupRow)
        SELECT s.TableName, 1, 1, 1 FROM dbo.Sync_Table s
         WHERE s.IsActive = 1 AND (@TableName IS NULL OR s.TableName = @TableName)
           AND NOT EXISTS (SELECT 1 FROM @flags f WHERE f.TableName = s.TableName);
    END
    ELSE
    BEGIN
        SET @source = 'Sync_Table';
        INSERT @flags (TableName, InsDel, Upd)
        SELECT TableName, 1, 1 FROM dbo.Sync_Table
         WHERE IsActive = 1 AND (@TableName IS NULL OR TableName = @TableName);
    END;

    DECLARE @result TABLE (TableName sysname, InsDelTrigger bit, UpdTrigger bit, Note nvarchar(400));
    DECLARE @table sysname, @insDel bit, @upd bit, @key sysname, @all bit, @vessel nvarchar(200), @sql nvarchar(max),
            @name sysname, @q nvarchar(300), @lit nvarchar(300), @note nvarchar(400);

    /* Triggers of tables that are not flagged any more (only when all tables are done). */
    IF @TableName IS NULL
    BEGIN
        DECLARE d CURSOR LOCAL FAST_FORWARD FOR
            SELECT tr.name FROM sys.triggers tr
             WHERE tr.parent_class = 1
               AND (tr.name LIKE N'TR[_]%[_]SyncRow' OR tr.name LIKE N'TR[_]%[_]SyncRow[_]InsDel' OR tr.name LIKE N'TR[_]%[_]SyncRow[_]Upd')
               AND NOT EXISTS (SELECT 1 FROM @flags f WHERE tr.parent_id = OBJECT_ID(N'dbo.' + QUOTENAME(f.TableName)) AND (f.InsDel = 1 OR f.Upd = 1));
        OPEN d; FETCH NEXT FROM d INTO @name;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @sql = N'DROP TRIGGER dbo.' + QUOTENAME(@name) + N';';
            EXEC sp_executesql @sql;
            FETCH NEXT FROM d INTO @name;
        END
        CLOSE d; DEALLOCATE d;
    END;

    DECLARE @noRow bit;
    DECLARE t CURSOR LOCAL FAST_FORWARD FOR SELECT TableName, InsDel, Upd, NoSetupRow FROM @flags ORDER BY TableName;
    OPEN t; FETCH NEXT FROM t INTO @table, @insDel, @upd, @noRow;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF OBJECT_ID(N'dbo.' + QUOTENAME(@table), N'U') IS NULL
        BEGIN
            IF @insDel = 1 OR @upd = 1
                INSERT @result (TableName, InsDelTrigger, UpdTrigger, Note) VALUES (@table, 0, 0, N'No such table in this database: skipped.');
            GOTO next_table;
        END;

        -- The key: a single-column primary key (or <table>ID), as the sync reads it.
        SET @key = dbo.Sync_KeyColumn(@table);
        SET @all = ISNULL((SELECT TOP (1) AllVessels FROM dbo.Sync_Table WHERE TableName = @table), 0);
        SET @vessel = CASE WHEN @all = 0 AND COL_LENGTH(N'dbo.' + QUOTENAME(@table), 'VesselID') IS NOT NULL
                           THEN N'CAST(VesselID AS int)' ELSE N'CAST(NULL AS int)' END;
        SET @q = QUOTENAME(@table);
        SET @lit = N'''' + REPLACE(@table, N'''', N'''''') + N'''';

        -- The older combined trigger is replaced by the two below (or removed when both flags are 0).
        IF OBJECT_ID(N'dbo.' + QUOTENAME(N'TR_' + @table + N'_SyncRow'), N'TR') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TRIGGER dbo.' + QUOTENAME(N'TR_' + @table + N'_SyncRow') + N';';
            EXEC sp_executesql @sql;
        END;

        IF @key IS NULL AND (@insDel = 1 OR @upd = 1)
        BEGIN
            INSERT @result (TableName, InsDelTrigger, UpdTrigger, Note) VALUES (@table, 0, 0, N'No single-column primary key: no trigger made.');
            SET @insDel = 0; SET @upd = 0;
        END;

        /* ---- Insert / delete ---- */
        SET @name = N'TR_' + @table + N'_SyncRow_InsDel';
        IF @insDel = 1
        BEGIN
            SET @sql = N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(@name) + N' ON dbo.' + @q + N'
AFTER INSERT, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows dbo.Sync_RowList;
    IF EXISTS (SELECT 1 FROM inserted)
    BEGIN
        INSERT @rows (KeyValue, VesselID)
        SELECT DISTINCT CAST(' + QUOTENAME(@key) + N' AS bigint), ' + @vessel + N' FROM inserted;
        EXEC dbo.Sync_LogChanges @TableName = ' + @lit + N', @Action = ''I'', @Rows = @rows;
    END
    ELSE IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        INSERT @rows (KeyValue, VesselID)
        SELECT DISTINCT CAST(' + QUOTENAME(@key) + N' AS bigint), ' + @vessel + N' FROM deleted;
        EXEC dbo.Sync_LogChanges @TableName = ' + @lit + N', @Action = ''D'', @Rows = @rows;
    END;
END;';
            EXEC sp_executesql @sql;
        END
        ELSE IF OBJECT_ID(N'dbo.' + QUOTENAME(@name), N'TR') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TRIGGER dbo.' + QUOTENAME(@name) + N';';
            EXEC sp_executesql @sql;
        END;

        /* ---- Update ---- */
        SET @name = N'TR_' + @table + N'_SyncRow_Upd';
        IF @upd = 1
        BEGIN
            -- Old and new rows both: a record moved to another vessel goes to the new one and is removed from the old one.
            SET @sql = N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(@name) + N' ON dbo.' + @q + N'
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    DECLARE @rows dbo.Sync_RowList;
    INSERT @rows (KeyValue, VesselID)
    SELECT CAST(' + QUOTENAME(@key) + N' AS bigint), ' + @vessel + N' FROM inserted
    UNION
    SELECT CAST(' + QUOTENAME(@key) + N' AS bigint), ' + @vessel + N' FROM deleted;
    EXEC dbo.Sync_LogChanges @TableName = ' + @lit + N', @Action = ''U'', @Rows = @rows;
END;';
            EXEC sp_executesql @sql;
        END
        ELSE IF OBJECT_ID(N'dbo.' + QUOTENAME(@name), N'TR') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TRIGGER dbo.' + QUOTENAME(@name) + N';';
            EXEC sp_executesql @sql;
        END;

        IF @insDel = 1 OR @upd = 1
        BEGIN
            SET @note = NULL;
            IF NOT EXISTS (SELECT 1 FROM dbo.Sync_Table s WHERE s.TableName = @table AND s.IsActive = 1)
                SET @note = N'Logged, but not in Sync_Table (active): the ships do not get this table.';
            ELSE IF @noRow = 1 AND @source = 'TableID_Setup'
                SET @note = N'No TableID_Setup row: both triggers kept because the ships get it. Add a row to set its flags.';
            INSERT @result (TableName, InsDelTrigger, UpdTrigger, Note) VALUES (@table, @insDel, @upd, @note);
        END;

        next_table:
        FETCH NEXT FROM t INTO @table, @insDel, @upd, @noRow;
    END
    CLOSE t; DEALLOCATE t;

    -- Tables the ships get that now have no trigger: changes at the office there would never reach a ship.
    IF @TableName IS NULL
        INSERT @result (TableName, InsDelTrigger, UpdTrigger, Note)
        SELECT s.TableName, 0, 0, N'In Sync_Table but no HQ trigger flag set in ' + @source + N': office changes are not sent to the ships.'
          FROM dbo.Sync_Table s
         WHERE s.IsActive = 1 AND NOT EXISTS (SELECT 1 FROM @result r WHERE r.TableName = s.TableName);

    SELECT TableName, InsDelTrigger, UpdTrigger, Note FROM @result ORDER BY CASE WHEN Note IS NULL THEN 1 ELSE 0 END, TableName, Note;
END
GO
