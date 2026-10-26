/* =====================================================================================================
   024_Vsl_TemplateRecords.sql  -  run on the OFFICE (HQ) database after 009 and 011. Safe to run more than once.
   Creates dbo.Vsl_CopyTemplateRecords and the table it keeps its work in (dbo.Vsl_TemplateCopy).

   VesselID = -9 marks TEMPLATE records: a set every vessel gets its own copy of (default folders, default
   settings rows, ...). A -9 record itself is never sent to a ship (the sync sends -1 and the vessel's own rows
   only; any other negative VesselID goes to nobody).

   dbo.Vsl_CopyTemplateRecords @VesselID = 101 gives vessel 101 its copy, in the office database:
     - every -9 record of a vessel table (TableID_Setup.IsForVsl = 1) is copied once for the vessel:
       same data, VesselID = 101, a new key from Proc_NextID (so it never clashes with the template or with
       another vessel's copy);
     - "once": dbo.Vsl_TemplateCopy remembers template -> copy per vessel, so running it again copies only
       templates added since (a vessel never gets two copies of the same template);
     - links between template records are moved to the copies: a column named like the key of a copied table
       (e.g. OtherDocument_FolderID) or a Parent... column pointing into its own table (e.g. ParentID) is set to
       the key of the vessel's copy when it pointed at a template;
     - all or nothing: one transaction.
   The copies are ordinary office records of that vessel, so they reach the ship with the vessel database
   (Vsl_CreateVesselDatabase runs this first) or with the row sync, and changes on board come back to them.

   Tables copied: a VesselID column and a single-column int / bigint primary key. A table with -9 records
   but another kind of key is listed and skipped.

   Usage:
     EXEC dbo.Vsl_CopyTemplateRecords @VesselID = 101;                 -- give vessel 101 its copy
     EXEC dbo.Vsl_CreateVesselDatabase @VesselID = 101;                -- does the same, then builds the database
   ===================================================================================================== */
SET NOCOUNT ON;
GO

/* Which template record became which record of which vessel. */
IF OBJECT_ID('dbo.Vsl_TemplateCopy', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Vsl_TemplateCopy
    (
        TableName    sysname          NOT NULL,
        TemplateKey  bigint           NOT NULL,   -- key of the -9 record
        VesselID     int              NOT NULL,
        NewKey       bigint           NOT NULL,   -- key of the vessel's copy
        RunID        uniqueidentifier NOT NULL,
        CopiedUtc    datetime2(0)     NOT NULL CONSTRAINT DF_Vsl_TemplateCopy_CopiedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Vsl_TemplateCopy PRIMARY KEY (TableName, TemplateKey, VesselID)
    );
    CREATE UNIQUE INDEX IX_Vsl_TemplateCopy_NewKey ON dbo.Vsl_TemplateCopy (TableName, NewKey);
    PRINT 'Created dbo.Vsl_TemplateCopy.';
END;
GO

CREATE OR ALTER PROCEDURE dbo.Vsl_CopyTemplateRecords
    @VesselID int,
    @Copied   int = NULL OUTPUT      -- number of records copied this time
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @run uniqueidentifier = NEWID(), @sql nvarchar(max), @table sysname, @key sysname, @identity bit,
            @n int, @first bigint, @cols nvarchar(max), @select nvarchar(max), @col sysname, @target sysname,
            @rows int;
    SET @Copied = 0;

    IF @VesselID IS NULL OR @VesselID <= 0
    BEGIN RAISERROR('Vsl_CopyTemplateRecords: give a real vessel (VesselID > 0).', 16, 1); RETURN; END;
    IF NOT EXISTS (SELECT 1 FROM dbo.V_Vessel WHERE VesselID = @VesselID)
    BEGIN RAISERROR('Vsl_CopyTemplateRecords: vessel %d is not in V_Vessel.', 16, 1, @VesselID); RETURN; END;
    IF OBJECT_ID('dbo.Proc_NextID', 'P') IS NULL
    BEGIN RAISERROR('Vsl_CopyTemplateRecords: dbo.Proc_NextID is missing (run 009_Proc_NextID.sql).', 16, 1); RETURN; END;
    IF COL_LENGTH('dbo.TableID_Setup', 'IsForVsl') IS NULL
    BEGIN RAISERROR('Vsl_CopyTemplateRecords: dbo.TableID_Setup has no IsForVsl column.', 16, 1); RETURN; END;

    /* ---- The vessel tables, with their key ------------------------------------------------------------- */
    -- COLLATE DATABASE_DEFAULT on every name column of the table variables: they are compared with this database's
    -- catalogue and tables, and without it they can take the server (tempdb) collation instead ("Cannot resolve the
    -- collation conflict between SQL_Latin1_General_CP1_CI_AS and Latin1_General_CI_AS").
    DECLARE @vslTables TABLE (TableName sysname COLLATE DATABASE_DEFAULT PRIMARY KEY);
    INSERT @vslTables (TableName)
    EXEC sp_executesql N'SELECT DISTINCT CAST(TableName AS sysname) COLLATE DATABASE_DEFAULT FROM dbo.TableID_Setup
                          WHERE IsForVsl = 1 AND OBJECT_ID(N''dbo.'' + QUOTENAME(TableName), N''U'') IS NOT NULL';

    -- Copyable: VesselID column + single-column int/bigint primary key.
    DECLARE @tables TABLE (TableName sysname COLLATE DATABASE_DEFAULT PRIMARY KEY, KeyColumn sysname COLLATE DATABASE_DEFAULT NULL,
                           IsIdentity bit NULL, Copyable bit NOT NULL);
    INSERT @tables (TableName, KeyColumn, IsIdentity, Copyable)
    SELECT v.TableName, k.KeyColumn, k.IsIdentity,
           CASE WHEN k.KeyColumn IS NOT NULL THEN 1 ELSE 0 END
      FROM @vslTables v
      OUTER APPLY (
          SELECT c.name AS KeyColumn, CAST(c.is_identity AS bit) AS IsIdentity
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
           WHERE i.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(v.TableName)) AND i.is_primary_key = 1
             AND TYPE_NAME(c.system_type_id) IN (N'int', N'bigint')
             AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 1
      ) k
     WHERE COL_LENGTH(N'dbo.' + QUOTENAME(v.TableName), 'VesselID') IS NOT NULL;

    BEGIN TRY
        BEGIN TRANSACTION;

        /* ---- 1. Copy the -9 records this vessel does not have yet ---------------------------------------- */
        DECLARE t CURSOR LOCAL FAST_FORWARD FOR SELECT TableName, KeyColumn, IsIdentity FROM @tables ORDER BY TableName;
        OPEN t; FETCH NEXT FROM t INTO @table, @key, @identity;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF @key IS NULL
            BEGIN
                SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@table) + N' WHERE VesselID = -9;';
                EXEC sp_executesql @sql, N'@n int OUTPUT', @n = @n OUTPUT;
                IF @n > 0 PRINT @table + ': ' + CAST(@n AS varchar(20)) + ' template record(s) skipped (no single int/bigint key).';
            END
            ELSE
            BEGIN
                SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@table) + N' s
                  WHERE s.VesselID = -9
                    AND NOT EXISTS (SELECT 1 FROM dbo.Vsl_TemplateCopy m
                                     WHERE m.TableName = @t AND m.VesselID = @v AND m.TemplateKey = s.' + QUOTENAME(@key) + N');';
                EXEC sp_executesql @sql, N'@t sysname, @v int, @n int OUTPUT', @t = @table, @v = @VesselID, @n = @n OUTPUT;

                IF @n > 0
                BEGIN
                    -- A block of new keys, one per record.
                    SET @first = NULL;
                    EXEC dbo.Proc_NextID @TableName = @table, @Origin = 'HQ', @VesselID = @VesselID,
                                         @Incrementor = @n, @NewTableID = @first OUTPUT;
                    IF @first IS NULL RAISERROR('Vsl_CopyTemplateRecords: no new key for dbo.%s.', 16, 1, @table);

                    -- template -> copy, in key order
                    SET @sql = N'INSERT dbo.Vsl_TemplateCopy (TableName, TemplateKey, VesselID, NewKey, RunID)
                    SELECT @t, s.' + QUOTENAME(@key) + N', @v,
                           @first - 1 + ROW_NUMBER() OVER (ORDER BY s.' + QUOTENAME(@key) + N'), @run
                      FROM dbo.' + QUOTENAME(@table) + N' s
                     WHERE s.VesselID = -9
                       AND NOT EXISTS (SELECT 1 FROM dbo.Vsl_TemplateCopy m
                                        WHERE m.TableName = @t AND m.VesselID = @v AND m.TemplateKey = s.' + QUOTENAME(@key) + N');';
                    EXEC sp_executesql @sql, N'@t sysname, @v int, @first bigint, @run uniqueidentifier',
                         @t = @table, @v = @VesselID, @first = @first, @run = @run;

                    -- The records: same data, new key, this vessel. (Computed and rowversion columns left out.)
                    SELECT @cols   = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id),
                           @select = STRING_AGG(CAST(CASE WHEN c.name = @key      THEN N'm.NewKey'
                                                          WHEN c.name = N'VesselID' THEN N'@v'
                                                          ELSE N's.' + QUOTENAME(c.name) END AS nvarchar(max)), N', ')
                                    WITHIN GROUP (ORDER BY c.column_id)
                      FROM sys.columns c
                     WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@table))
                       AND c.is_computed = 0 AND TYPE_NAME(c.system_type_id) NOT IN (N'timestamp', N'rowversion');

                    SET @sql = CASE WHEN @identity = 1 THEN N'SET IDENTITY_INSERT dbo.' + QUOTENAME(@table) + N' ON;' ELSE N'' END + N'
                    INSERT dbo.' + QUOTENAME(@table) + N' (' + @cols + N')
                    SELECT ' + @select + N'
                      FROM dbo.' + QUOTENAME(@table) + N' s
                      JOIN dbo.Vsl_TemplateCopy m ON m.TableName = @t AND m.VesselID = @v AND m.RunID = @run
                                                 AND m.TemplateKey = s.' + QUOTENAME(@key) + N'
                     WHERE s.VesselID = -9;
                    SET @rows = @@ROWCOUNT;' +
                    CASE WHEN @identity = 1 THEN N'
                    SET IDENTITY_INSERT dbo.' + QUOTENAME(@table) + N' OFF;' ELSE N'' END;
                    EXEC sp_executesql @sql, N'@t sysname, @v int, @run uniqueidentifier, @rows int OUTPUT',
                         @t = @table, @v = @VesselID, @run = @run, @rows = @rows OUTPUT;

                    SET @Copied += @rows;
                    PRINT @table + ': ' + CAST(@rows AS varchar(20)) + ' template record(s) copied for vessel ' + CAST(@VesselID AS varchar(10)) + '.';
                END
            END;
            FETCH NEXT FROM t INTO @table, @key, @identity;
        END
        CLOSE t; DEALLOCATE t;

        /* ---- 2. Links between templates point at the vessel's copies --------------------------------------- */
        -- For each table copied now: a column named like the key of a copyable table (or a Parent... column of its
        -- own table) that still holds a template key is changed to the key of this vessel's copy of that template.
        IF @Copied > 0
        BEGIN
            DECLARE @links TABLE (TableName sysname COLLATE DATABASE_DEFAULT, KeyColumn sysname COLLATE DATABASE_DEFAULT,
                                  ColumnName sysname COLLATE DATABASE_DEFAULT, TargetTable sysname COLLATE DATABASE_DEFAULT,
                                  TargetKey sysname COLLATE DATABASE_DEFAULT);
            INSERT @links (TableName, KeyColumn, ColumnName, TargetTable, TargetKey)
            SELECT DISTINCT src.TableName, src.KeyColumn, c.name, tgt.TableName, tgt.KeyColumn
              FROM @tables src
              JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(src.TableName))
              JOIN @tables tgt   ON tgt.KeyColumn IS NOT NULL
                                AND (   (c.name = tgt.KeyColumn AND NOT (tgt.TableName = src.TableName))
                                     OR (tgt.TableName = src.TableName AND c.name LIKE N'Parent%'))
             WHERE src.KeyColumn IS NOT NULL
               AND c.name <> src.KeyColumn AND c.name <> N'VesselID' AND c.is_computed = 0
               AND TYPE_NAME(c.system_type_id) IN (N'int', N'bigint')
               AND EXISTS (SELECT 1 FROM dbo.Vsl_TemplateCopy r WHERE r.TableName = src.TableName AND r.VesselID = @VesselID AND r.RunID = @run);

            DECLARE l CURSOR LOCAL FAST_FORWARD FOR SELECT TableName, KeyColumn, ColumnName, TargetTable FROM @links;
            OPEN l; FETCH NEXT FROM l INTO @table, @key, @col, @target;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @sql = N'UPDATE x SET x.' + QUOTENAME(@col) + N' = tm.NewKey
                  FROM dbo.' + QUOTENAME(@table) + N' x
                  JOIN dbo.Vsl_TemplateCopy sm ON sm.TableName = @t AND sm.VesselID = @v AND sm.RunID = @run
                                              AND sm.NewKey = x.' + QUOTENAME(@key) + N'
                  JOIN dbo.Vsl_TemplateCopy tm ON tm.TableName = @target AND tm.VesselID = @v
                                              AND tm.TemplateKey = x.' + QUOTENAME(@col) + N';
                SET @rows = @@ROWCOUNT;';
                EXEC sp_executesql @sql, N'@t sysname, @target sysname, @v int, @run uniqueidentifier, @rows int OUTPUT',
                     @t = @table, @target = @target, @v = @VesselID, @run = @run, @rows = @rows OUTPUT;
                IF @rows > 0
                    PRINT @table + '.' + @col + ': ' + CAST(@rows AS varchar(20)) + ' link(s) moved to the vessel''s copies of ' + @target + '.';
                FETCH NEXT FROM l INTO @table, @key, @col, @target;
            END
            CLOSE l; DEALLOCATE l;
        END;

        COMMIT TRANSACTION;
        IF @Copied = 0 PRINT 'Vessel ' + CAST(@VesselID AS varchar(10)) + ' already has every template record (nothing copied).';
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @Copied = 0;
        THROW;
    END CATCH
END
GO

PRINT '024_Vsl_TemplateRecords: dbo.Vsl_CopyTemplateRecords is ready. Run 011_Vsl_CreateVesselDatabase.sql again so it calls it.';
GO
