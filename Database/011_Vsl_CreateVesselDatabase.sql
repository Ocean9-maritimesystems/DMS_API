/* =====================================================================================================
   011_Vsl_CreateVesselDatabase.sql  -  run on the OFFICE (HQ) database. Creates dbo.Vsl_CreateVesselDatabase.

   Builds a vessel (ship) database from the office database, on the same SQL Server:

     1. Tables     every table in dbo.TableID_Setup with IsForVsl = 1, with the office columns, types, sizes,
                   nullability, defaults and primary key (no triggers, no foreign keys, no identity: on board the
                   keys come from the office or from Proc_NextID, never from an identity column).
     0. Templates  records with VesselID = -9 are templates: first the vessel gets its own copy of them in this
                   office database (VesselID = @VesselID, new keys), once per template - dbo.Vsl_CopyTemplateRecords,
                   024_Vsl_TemplateRecords.sql. The copies are then copied to the ship like the vessel's other
                   records; the -9 templates themselves are not.
     2. Records    copied exactly - same keys, same data - for:
                       VesselID = -1          (records for all vessels)
                       VesselID = @VesselID   (records for this vessel, its template copies included)
                   A table without a VesselID column is copied whole.
     3. Ship parts what DMS.Vessel.Api needs on top of the office tables: IsPendingUpload on the tables written on
                   board, V_Vessel.TableIDstart, Vsl_Staff.Fleet_CrewID (= office Vsl_StaffID) and a Password column
                   wide enough for a hash, the SyncState / SyncLog / SyncDownload tables, TableID_Setup and
                   Proc_NextID, and the EF migration history (so "dotnet ef database update" adds nothing twice).
                   Then the ship schema, the same steps as DMS.Vessel.Api ShipSchema.cs: the new feature tables and
                   columns when the office did not send them, the folder columns (no Scope / OwnerUserID / ViewRoles /
                   EditRoles), and the app's stored procedures Ship_GetPending, Ship_ColumnIsIdentity and
                   Ship_ApplyRowChange. The vessel API repeats these at every start, so the two never disagree.
     4. Sync       this vessel's waiting rows in Sync_ChangeLog up to now are marked downloaded: the new database
                   already holds those records. Changes made while the copy runs stay waiting and arrive by sync.

   Crew passwords are copied as the office holds them (plain text); login on board compares them as they are.

   Usage:
     EXEC dbo.Vsl_CreateVesselDatabase @VesselID = 101;                                   -- DMS_Vessel_101
     EXEC dbo.Vsl_CreateVesselDatabase @VesselID = 101, @DatabaseName = N'DMS_Vesselx',
                                       @DropExisting = 1, @IncludeFiles = 0;
     EXEC dbo.Vsl_CreateVesselDatabase @VesselID = 101, @CopyTemplates = 0;              -- no -9 template copies
   Then back up the new database and restore it on the ship server (or point the ship API at it).
   ===================================================================================================== */

SET NOCOUNT ON;
GO

/* The list of tables to copy (filled from TableID_Setup with INSERT ... EXEC). */
IF TYPE_ID('dbo.Vsl_TableList') IS NULL CREATE TYPE dbo.Vsl_TableList AS TABLE (TableName sysname PRIMARY KEY);
GO

CREATE OR ALTER PROCEDURE dbo.Vsl_CreateVesselDatabase
    @VesselID      int,
    @DatabaseName  sysname = NULL,   -- default DMS_Vessel_<VesselID>
    @DropExisting  bit = 0,          -- 1 = drop a database of that name first (it is replaced)
    @IncludeFiles  bit = 1,          -- 0 = file contents (varbinary(max)) left empty: downloaded on board when opened
    @CopyTemplates bit = 1           -- 1 = give the vessel its copy of the VesselID = -9 template records first (024)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @db nvarchar(300), @exec nvarchar(400), @sql nvarchar(max), @table sysname, @cols nvarchar(max),
            @select nvarchar(max), @pk nvarchar(max), @where nvarchar(400), @rows int, @lastChange bigint,
            @proc nvarchar(max);

    /* ---- Checks ---------------------------------------------------------------------------------------- */
    IF NOT EXISTS (SELECT 1 FROM dbo.V_Vessel WHERE VesselID = @VesselID)
    BEGIN RAISERROR('Vessel %d is not in V_Vessel.', 16, 1, @VesselID); RETURN; END;

    IF COL_LENGTH('dbo.TableID_Setup', 'IsForVsl') IS NULL
    BEGIN RAISERROR('dbo.TableID_Setup has no IsForVsl column.', 16, 1); RETURN; END;

    SET @DatabaseName = ISNULL(NULLIF(LTRIM(RTRIM(@DatabaseName)), N''), N'DMS_Vessel_' + CAST(@VesselID AS nvarchar(20)));
    IF @DatabaseName = DB_NAME()
    BEGIN RAISERROR('The vessel database cannot have the same name as this (office) database.', 16, 1); RETURN; END;
    
    SET @db = QUOTENAME(@DatabaseName);
    SET @exec = @db + N'.sys.sp_executesql';

    -- The tables for vessels (IsForVsl = 1) that exist here.
    DECLARE @tables dbo.Vsl_TableList;
    SET @sql = N'SELECT DISTINCT CAST(TableName AS sysname) FROM dbo.TableID_Setup
                  WHERE IsForVsl = 1 AND OBJECT_ID(N''dbo.'' + QUOTENAME(TableName), N''U'') IS NOT NULL';
    INSERT @tables (TableName) EXEC sp_executesql @sql;
   
   IF NOT EXISTS (SELECT 1 FROM @tables)
    BEGIN RAISERROR('No table in dbo.TableID_Setup has IsForVsl = 1.', 16, 1); RETURN; END;

    /* ---- 0. The vessel's own copy of the template records (VesselID = -9) --------------------------------- */
    -- Made in this office database first, so the copies are ordinary records of the vessel: they are copied to
    -- the ship below with its other records, and changes on board sync back to them.
    IF @CopyTemplates = 1
    BEGIN
        IF OBJECT_ID('dbo.Vsl_CopyTemplateRecords', 'P') IS NULL
            PRINT 'Warning: dbo.Vsl_CopyTemplateRecords is missing (run 024_Vsl_TemplateRecords.sql): -9 template records not copied.';
        ELSE
            EXEC dbo.Vsl_CopyTemplateRecords @VesselID = @VesselID;
    END;

    /* ---- 1. The database --------------------------------------------------------------------------------- */
    IF DB_ID(@DatabaseName) IS NOT NULL
    BEGIN
        IF @DropExisting = 0
        BEGIN RAISERROR('Database %s already exists. Use @DropExisting = 1 to replace it.', 16, 1, @DatabaseName); RETURN; END;

        SET @sql = N'ALTER DATABASE ' + @db + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + @db + N';';
        EXEC sp_executesql @sql;
    END;

    SET @sql = N'CREATE DATABASE ' + @db + N';';
    EXEC sp_executesql @sql;
    PRINT 'Database ' + @DatabaseName + ' created.';

    -- Sync rows waiting for this vessel up to now: their records are in the copy (marked downloaded at the end).
    IF OBJECT_ID('dbo.Sync_ChangeLog', 'U') IS NOT NULL
        SELECT @lastChange = MAX(SyncChangeID) FROM dbo.Sync_ChangeLog WHERE VesselID = @VesselID AND DownloadedUtc IS NULL;

    /* ---- 2. Tables and records --------------------------------------------------------------------------- */
    DECLARE t CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @tables ORDER BY TableName;
    OPEN t; FETCH NEXT FROM t INTO @table;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Columns as at the office (identity left out; computed columns kept as computed).
        SELECT @cols = STRING_AGG(CAST(
                   QUOTENAME(c.name) + N' ' +
                   CASE WHEN cc.definition IS NOT NULL THEN N'AS ' + cc.definition
                   ELSE
                       CASE
                           WHEN ty.name IN (N'varchar', N'char', N'varbinary', N'binary')
                               THEN ty.name + N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length AS nvarchar(10)) END + N')'
                           WHEN ty.name IN (N'nvarchar', N'nchar')
                               THEN ty.name + N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length / 2 AS nvarchar(10)) END + N')'
                           WHEN ty.name IN (N'decimal', N'numeric')
                               THEN ty.name + N'(' + CAST(c.precision AS nvarchar(5)) + N',' + CAST(c.scale AS nvarchar(5)) + N')'
                           WHEN ty.name IN (N'datetime2', N'time', N'datetimeoffset')
                               THEN ty.name + N'(' + CAST(c.scale AS nvarchar(5)) + N')'
                           WHEN ty.name = N'timestamp' THEN N'binary(8)'
                           ELSE ty.name
                       END
                       + CASE WHEN c.collation_name IS NOT NULL THEN N' COLLATE ' + c.collation_name ELSE N'' END
                       + CASE WHEN c.is_nullable = 1 THEN N' NULL' ELSE N' NOT NULL' END
                       + ISNULL(N' CONSTRAINT ' + QUOTENAME(dc.name) + N' DEFAULT ' + dc.definition, N'')
                   END AS nvarchar(max)), N',' + CHAR(10) + N'    ') WITHIN GROUP (ORDER BY c.column_id)
          FROM sys.columns c
          JOIN sys.types ty ON ty.user_type_id = c.system_type_id AND ty.user_type_id = ty.system_type_id
          LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
          LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
         WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@table));

        -- Primary key, as at the office.
        SELECT @pk = N',' + CHAR(10) + N'    CONSTRAINT ' + QUOTENAME(k.name) + N' PRIMARY KEY '
                   + CASE WHEN i.type = 1 THEN N'CLUSTERED' ELSE N'NONCLUSTERED' END + N' ('
                   + STRING_AGG(QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END, N', ')
                         WITHIN GROUP (ORDER BY ic.key_ordinal) + N')'
          FROM sys.key_constraints k
          JOIN sys.indexes i        ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
          JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
          JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
         WHERE k.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@table)) AND k.type = 'PK'
         GROUP BY k.name, i.type;

        SET @sql = N'CREATE TABLE dbo.' + QUOTENAME(@table) + N'
(
    ' + @cols + ISNULL(@pk, N'') + N'
);';
        EXEC @exec @sql;
        SET @pk = NULL;

        -- The records: VesselID -1 and this vessel (all records when the table has no VesselID column).
        SELECT @cols   = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id),
               @select = STRING_AGG(CAST(CASE WHEN @IncludeFiles = 0 AND ty.name IN (N'varbinary', N'image') AND c.max_length = -1
                                              AND c.is_nullable = 1
                                         THEN N'NULL' ELSE QUOTENAME(c.name) END AS nvarchar(max)), N', ')
                        WITHIN GROUP (ORDER BY c.column_id)
          FROM sys.columns c
          JOIN sys.types ty ON ty.user_type_id = c.system_type_id AND ty.user_type_id = ty.system_type_id
         WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@table)) AND c.is_computed = 0;

        -- (-9 = templates: not copied; the vessel's copies of them have VesselID = @VesselID.)
        SET @where = CASE WHEN COL_LENGTH(N'dbo.' + QUOTENAME(@table), 'VesselID') IS NOT NULL
                          THEN N' WHERE VesselID IN (-1, @VesselID)' ELSE N'' END;
        -- Office-only records (Other Documents added at the office, 017) stay at the office.
        IF COL_LENGTH(N'dbo.' + QUOTENAME(@table), 'IsForOfficeOnly') IS NOT NULL
            SET @where = CASE WHEN @where = N'' THEN N' WHERE' ELSE @where + N' AND' END + N' ISNULL(IsForOfficeOnly, 0) = 0';
        SET @sql = N'INSERT ' + @db + N'.dbo.' + QUOTENAME(@table) + N' (' + @cols + N')
SELECT ' + @select + N' FROM dbo.' + QUOTENAME(@table) + @where + N';
SET @rows = @@ROWCOUNT;';
        EXEC sp_executesql @sql, N'@VesselID int, @rows int OUTPUT', @VesselID = @VesselID, @rows = @rows OUTPUT;
        PRINT @table + ': ' + CAST(@rows AS varchar(20)) + ' record(s)' + CASE WHEN @where = N'' THEN ' (no VesselID column: all)' ELSE '' END;

        FETCH NEXT FROM t INTO @table;
    END
    CLOSE t; DEALLOCATE t;

    /* ---- 3. What the ship app needs on top of the office tables ------------------------------------------ */
    SET @sql = N'
-- Written on board and not at the office yet.
IF OBJECT_ID(N''dbo.DM_Manual_UserAck'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.DM_Manual_UserAck'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.DM_Manual_UserAck ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_DM_Manual_UserAck_IsPendingUpload DEFAULT (0);
IF OBJECT_ID(N''dbo.CIR_UserAck'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.CIR_UserAck'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.CIR_UserAck ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_CIR_UserAck_IsPendingUpload DEFAULT (0);
IF OBJECT_ID(N''dbo.OtherDocumentMaster'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.OtherDocumentMaster'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.OtherDocumentMaster ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_OtherDocumentMaster_IsPendingUpload DEFAULT (0);
IF OBJECT_ID(N''dbo.OtherDocument_Blob'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.OtherDocument_Blob'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.OtherDocument_Blob ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_OtherDocument_Blob_IsPendingUpload DEFAULT (0);
-- New feature tables (012_NewFeatures.sql) written on board. The ship migration 20261016120000_NewFeatures is left
-- to run when the ship app starts: it only adds what is still missing.
IF OBJECT_ID(N''dbo.DMS_InboxRead'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.DMS_InboxRead'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.DMS_InboxRead ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_DMS_InboxRead_IsPendingUpload DEFAULT (0);
IF OBJECT_ID(N''dbo.SUG_Master'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.SUG_Master'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.SUG_Master ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_SUG_Master_IsPendingUpload DEFAULT (0);
IF OBJECT_ID(N''dbo.SUG_Log'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.SUG_Log'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.SUG_Log ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_SUG_Log_IsPendingUpload DEFAULT (0);
IF OBJECT_ID(N''dbo.SUG_Blob'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.SUG_Blob'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.SUG_Blob ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_SUG_Blob_IsPendingUpload DEFAULT (0);
IF OBJECT_ID(N''dbo.OtherDocument_Folder'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.OtherDocument_Folder'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.OtherDocument_Folder ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_OtherDocument_Folder_IsPendingUpload DEFAULT (0);
IF OBJECT_ID(N''dbo.OtherDocument_Log'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.OtherDocument_Log'', N''IsPendingUpload'') IS NULL
    ALTER TABLE dbo.OtherDocument_Log ADD IsPendingUpload bit NOT NULL CONSTRAINT DF_OtherDocument_Log_IsPendingUpload DEFAULT (0);
';
    EXEC @exec @sql;

    -- Documents filed in folders: the folder, and a folder move made on board that is not at the office yet.
    SET @sql = N'
IF COL_LENGTH(''dbo.OtherDocumentMaster'', ''FolderPendingUpload'') IS NULL
    ALTER TABLE dbo.OtherDocumentMaster ADD FolderPendingUpload bit NOT NULL CONSTRAINT DF_OtherDocumentMaster_FolderPendingUpload DEFAULT (0);';
    EXEC @exec @sql;

    -- Proc_NextID: the same procedure as here, so IDs on board come from this vessel's range.
    SET @proc = OBJECT_DEFINITION(OBJECT_ID('dbo.Proc_NextID'));
    IF @proc IS NULL
        PRINT 'Warning: dbo.Proc_NextID is not in this database (run 009_Proc_NextID.sql here, then on the ship database).';
    ELSE
        EXEC @exec @proc;

    -- Tables the ship app reads that were not in the IsForVsl list.
    DECLARE @missing nvarchar(max);
    SELECT @missing = STRING_AGG(n.TableName, N', ')
      FROM (VALUES (N'V_Vessel'), (N'DM_Status'), (N'DM_Settings'), (N'CIR_Settings'), (N'Vsl_Staff'), (N'DM_Category'),
                   (N'DM_Manual'), (N'DM_ManualVersion'), (N'DM_Manual_Attachments'), (N'DM_Manual_ToAck_Vsl'),
                   (N'DM_Manual_UserAck'), (N'CIR_Category'), (N'CIR_Master'), (N'CIR_Blob'), (N'CIR_ToAck_Vsl'),
                   (N'CIR_UserAck'), (N'OtherDocument_Type'), (N'OtherDocumentMaster'), (N'OtherDocument_Blob')) n (TableName)
     WHERE NOT EXISTS (SELECT 1 FROM @tables t WHERE t.TableName = n.TableName);
    IF @missing IS NOT NULL
        PRINT 'Warning: the ship app also uses these tables, which are not marked IsForVsl = 1: ' + @missing;

    /* ---- 4. Sync: what is in the copy is not sent again ---------------------------------------------------- */
    IF @lastChange IS NOT NULL
    BEGIN
        UPDATE dbo.Sync_ChangeLog SET SentUtc = ISNULL(SentUtc, SYSUTCDATETIME()), DownloadedUtc = SYSUTCDATETIME()
         WHERE VesselID = @VesselID AND DownloadedUtc IS NULL AND SyncChangeID <= @lastChange;
        PRINT CAST(@@ROWCOUNT AS varchar(20)) + ' waiting sync row(s) for vessel ' + CAST(@VesselID AS varchar(10)) + ' marked downloaded.';
    END;

    PRINT 'Vessel database ' + @DatabaseName + ' is ready for vessel ' + CAST(@VesselID AS varchar(10)) + '.';
END
GO
