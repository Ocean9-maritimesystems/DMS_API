/* =====================================================================================================
   030_Ship_CreateLogTriggers.sql  -  creates (or replaces) dbo.Ship_CreateLogTriggers. Does not run it.

   Run on the OFFICE database: Vsl_CreateVesselDatabase (011) copies every office procedure to a new ship database
   and runs this one there. Also run it on a ship database built before 011 kept the office collation.
   This version names the collation (COLLATE DATABASE_DEFAULT) wherever table names are compared, so it is made
   even when a column keeps another collation than the database ("Cannot resolve the collation conflict ...").

   On a ship database:  EXEC dbo.Ship_CreateLogTriggers;   (triggers from TableID_Setup.Vsl_Has_InsDEL_Trigger /
                        Vsl_Has_Upd_Trigger; never run it on the office database)
   ===================================================================================================== */
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.Ship_CreateLogTriggers
    @TableName sysname = NULL       -- one table, or NULL = every table in TableID_Setup
AS
BEGIN
    /* The change-log triggers of the ship database, as dbo.TableID_Setup says per table:
         Vsl_Has_InsDEL_Trigger = 1  ->  TR_<table>_ShipLog_InsDel  AFTER INSERT, DELETE   (logs I / D)
         Vsl_Has_Upd_Trigger    = 1  ->  TR_<table>_ShipLog_Upd     AFTER UPDATE           (logs U)
       A flag at 0 removes that trigger. Each trigger writes the record's key to dbo.Ship_ChangeLog, which the sync
       sends to the office; writes made by the sync itself (SESSION_CONTEXT 'Sync' = 1) are not logged.
       The older single trigger TR_<table>_ShipLog is replaced by these two.
       A table needs a single-column int / bigint primary key; otherwise it is skipped (listed in the result).
       If TableID_Setup has no flag columns (an old ship database), every active table in Ship_SyncTable gets both.
       Returns one row per table: what it has now, and a note when something needs a look. */
    SET NOCOUNT ON;

    IF OBJECT_ID(N'dbo.Ship_ChangeLog', N'U') IS NULL
    BEGIN RAISERROR('Ship_CreateLogTriggers: dbo.Ship_ChangeLog is missing (the vessel API creates it at start).', 16, 1); RETURN; END;

    DECLARE @flags TABLE (TableName sysname COLLATE DATABASE_DEFAULT PRIMARY KEY, InsDel bit NOT NULL, Upd bit NOT NULL);
    DECLARE @source varchar(20);

    IF OBJECT_ID(N'dbo.TableID_Setup', N'U') IS NOT NULL
       AND COL_LENGTH('dbo.TableID_Setup', 'Vsl_Has_InsDEL_Trigger') IS NOT NULL
       AND COL_LENGTH('dbo.TableID_Setup', 'Vsl_Has_Upd_Trigger') IS NOT NULL
    BEGIN
        SET @source = 'TableID_Setup';
        -- A table can have several rows (per origin / vessel): a flag set on any of them counts.
        INSERT @flags (TableName, InsDel, Upd)
        EXEC sp_executesql N'
            SELECT LTRIM(RTRIM(TableName)) COLLATE DATABASE_DEFAULT,
                   MAX(CASE WHEN ISNULL(Vsl_Has_InsDEL_Trigger, 0) = 1 THEN 1 ELSE 0 END),
                   MAX(CASE WHEN ISNULL(Vsl_Has_Upd_Trigger, 0) = 1 THEN 1 ELSE 0 END)
              FROM dbo.TableID_Setup
             WHERE NULLIF(LTRIM(RTRIM(TableName)), '''') IS NOT NULL
               AND (@TableName IS NULL OR LTRIM(RTRIM(TableName)) COLLATE DATABASE_DEFAULT = @TableName)
             GROUP BY LTRIM(RTRIM(TableName)) COLLATE DATABASE_DEFAULT;', N'@TableName sysname', @TableName = @TableName;
    END
    ELSE IF OBJECT_ID(N'dbo.Ship_SyncTable', N'U') IS NOT NULL
    BEGIN
        SET @source = 'Ship_SyncTable';
        INSERT @flags (TableName, InsDel, Upd)
        SELECT TableName COLLATE DATABASE_DEFAULT, 1, 1 FROM dbo.Ship_SyncTable
         WHERE IsActive = 1 AND (@TableName IS NULL OR TableName COLLATE DATABASE_DEFAULT = @TableName);
    END
    ELSE
    BEGIN RAISERROR('Ship_CreateLogTriggers: neither TableID_Setup (with Vsl_Has_* columns) nor Ship_SyncTable is there.', 16, 1); RETURN; END;

    DECLARE @result TABLE (TableName sysname COLLATE DATABASE_DEFAULT, InsDelTrigger bit, UpdTrigger bit, Note nvarchar(400));
    DECLARE @table sysname, @insDel bit, @upd bit, @key sysname, @sql nvarchar(max), @name sysname, @q nvarchar(300), @lit nvarchar(300);

    /* Triggers of tables that are not in the list any more (only when all tables are done). */
    IF @TableName IS NULL
    BEGIN
        DECLARE d CURSOR LOCAL FAST_FORWARD FOR
            SELECT tr.name FROM sys.triggers tr
             WHERE tr.parent_class = 1
               AND (tr.name LIKE N'TR[_]%[_]ShipLog' OR tr.name LIKE N'TR[_]%[_]ShipLog[_]InsDel' OR tr.name LIKE N'TR[_]%[_]ShipLog[_]Upd')
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

    DECLARE t CURSOR LOCAL FAST_FORWARD FOR SELECT TableName, InsDel, Upd FROM @flags ORDER BY TableName;
    OPEN t; FETCH NEXT FROM t INTO @table, @insDel, @upd;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF OBJECT_ID(N'dbo.' + QUOTENAME(@table), N'U') IS NULL
        BEGIN
            IF @insDel = 1 OR @upd = 1
                INSERT @result (TableName, InsDelTrigger, UpdTrigger, Note) VALUES (@table, 0, 0, N'No such table on this ship: skipped.');
            GOTO next_table;
        END;

        SET @key = NULL;
        SELECT @key = c.name
          FROM sys.indexes i
          JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
          JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
         WHERE i.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@table), N'U') AND i.is_primary_key = 1
           AND TYPE_NAME(c.system_type_id) IN (N'int', N'bigint')
           AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 1;

        SET @q = QUOTENAME(@table);
        SET @lit = N'''' + REPLACE(@table, N'''', N'''''') + N'''';

        -- The older combined trigger is replaced by the two below (or removed when both flags are 0).
        IF OBJECT_ID(N'dbo.' + QUOTENAME(N'TR_' + @table + N'_ShipLog'), N'TR') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TRIGGER dbo.' + QUOTENAME(N'TR_' + @table + N'_ShipLog') + N';';
            EXEC sp_executesql @sql;
        END;

        IF @key IS NULL AND (@insDel = 1 OR @upd = 1)
        BEGIN
            INSERT @result (TableName, InsDelTrigger, UpdTrigger, Note) VALUES (@table, 0, 0, N'No single int / bigint primary key: no trigger made.');
            SET @insDel = 0; SET @upd = 0;
        END;

        /* ---- Insert / delete ---- */
        SET @name = N'TR_' + @table + N'_ShipLog_InsDel';
        IF @insDel = 1
        BEGIN
            SET @sql = N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(@name) + N' ON dbo.' + @q + N'
AFTER INSERT, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF CAST(SESSION_CONTEXT(N''Sync'') AS int) = 1 RETURN;   -- written by the sync itself
    INSERT dbo.Ship_ChangeLog (TableName, KeyValue, Action, CreatedUtc)
    SELECT ' + @lit + N', CAST(i.' + QUOTENAME(@key) + N' AS bigint), ''I'', SYSUTCDATETIME() FROM inserted i
    UNION ALL
    SELECT ' + @lit + N', CAST(d.' + QUOTENAME(@key) + N' AS bigint), ''D'', SYSUTCDATETIME() FROM deleted d;
END;';
            EXEC sp_executesql @sql;
        END
        ELSE IF OBJECT_ID(N'dbo.' + QUOTENAME(@name), N'TR') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TRIGGER dbo.' + QUOTENAME(@name) + N';';
            EXEC sp_executesql @sql;
        END;

        /* ---- Update ---- */
        SET @name = N'TR_' + @table + N'_ShipLog_Upd';
        IF @upd = 1
        BEGIN
            -- A key changed by the update: the old key is logged as removed.
            SET @sql = N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(@name) + N' ON dbo.' + @q + N'
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF CAST(SESSION_CONTEXT(N''Sync'') AS int) = 1 RETURN;   -- written by the sync itself
    INSERT dbo.Ship_ChangeLog (TableName, KeyValue, Action, CreatedUtc)
    SELECT ' + @lit + N', CAST(i.' + QUOTENAME(@key) + N' AS bigint), ''U'', SYSUTCDATETIME() FROM inserted i
    UNION ALL
    SELECT ' + @lit + N', CAST(d.' + QUOTENAME(@key) + N' AS bigint), ''D'', SYSUTCDATETIME() FROM deleted d
     WHERE NOT EXISTS (SELECT 1 FROM inserted i WHERE i.' + QUOTENAME(@key) + N' = d.' + QUOTENAME(@key) + N');
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
            DECLARE @note nvarchar(400) = NULL;
            IF OBJECT_ID(N'dbo.Ship_SyncTable', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM dbo.Ship_SyncTable s WHERE s.TableName COLLATE DATABASE_DEFAULT = @table AND s.IsActive = 1)
                SET @note = N'Logged, but not in Ship_SyncTable (active): its changes are not sent to the office.';
            INSERT @result (TableName, InsDelTrigger, UpdTrigger, Note) VALUES (@table, @insDel, @upd, @note);
        END;

        next_table:
        FETCH NEXT FROM t INTO @table, @insDel, @upd;
    END
    CLOSE t; DEALLOCATE t;

    -- Tables the sync sends that now have no trigger: changes made on board there would never go to the office.
    IF OBJECT_ID(N'dbo.Ship_SyncTable', N'U') IS NOT NULL AND @TableName IS NULL
        INSERT @result (TableName, InsDelTrigger, UpdTrigger, Note)
        SELECT s.TableName COLLATE DATABASE_DEFAULT, 0, 0, N'In Ship_SyncTable but no trigger flag set in ' + @source + N': changes on board are not sent.'
          FROM dbo.Ship_SyncTable s
         WHERE s.IsActive = 1 AND NOT EXISTS (SELECT 1 FROM @result r WHERE r.TableName = s.TableName COLLATE DATABASE_DEFAULT);

    SELECT TableName, InsDelTrigger, UpdTrigger, Note FROM @result ORDER BY CASE WHEN Note IS NULL THEN 1 ELSE 0 END, TableName;
END
GO
