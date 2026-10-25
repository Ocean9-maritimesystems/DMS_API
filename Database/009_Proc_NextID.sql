/* =====================================================================================================
   009_Proc_NextID.sql  -  one "next ID" procedure for every table, in the office (HQ) and ship (VSL) databases.

   Replaces the per-table procedures such as Proc_NextID_App_Images. Same rules:
     - The new ID is the higher of MAX(primary key) for this origin / vessel and the last number handed out
       (TableID_Setup.LastNumber), plus 1. So a number is never reused after its row is deleted, and a vessel's
       numbers start from its own range.
     - @Incrementor reserves a block: IDs @NewTableID .. @NewTableID + @Incrementor - 1 are all yours.

   The counter row in TableID_Setup (one per table, origin and vessel):
     - Origin 'VSL' : the row with TableName, Origin = 'VSL' and VesselID = @VesselID is read. When there is none,
                      it is inserted with LastNumber = that vessel's TableIDstart (V_Vessel), so the vessel's first ID is TableIDstart + 1. MAX is taken over the table's
                      Origin = 'VSL' rows of @VesselID only.
     - Origin 'HQ'  : the row with TableName, Origin = 'HQ' and VesselID = 0 (inserted with LastNumber 0 when
                      there is none). MAX is taken over Origin = 'HQ' rows, whatever their VesselID.
     A table with no Origin or no VesselID column is simply not filtered on it.
     If TableID_Setup has no Origin column yet (an older ship database), the row is found by VesselID alone.

   Safety net: when the chosen number (or block) is already used by another row of the table - another origin
   or vessel, or a TableIDstart that is missing or overlaps - numbering carries on after the highest ID in the
   whole table instead.

   The primary key column is read from the table itself (single-column primary key), falling back to
   <TableName>ID. The table name is checked against the database, so it cannot be used to inject SQL.

   Usage:
     DECLARE @id bigint;
     EXEC dbo.Proc_NextID @TableName = 'OtherDocumentMaster', @Origin = 'VSL', @VesselID = 101,
                          @Incrementor = 1, @NewTableID = @id OUTPUT;
     SELECT @id;

   Run on the office database and on each ship database (011_Vsl_CreateVesselDatabase.sql copies it to a new
   ship database). Needs TableID_Setup, and V_Vessel with a TableIDstart column for the VSL start
   numbers (without it, a vessel's numbering starts from 0).
   ===================================================================================================== */

/* TableID_Setup.Origin: which origin a counter row belongs to ('HQ' or 'VSL'). Added when missing; existing rows
   get 'HQ' for VesselID 0 and 'VSL' for a vessel. Rows with VesselID -1 (the IsForVsl list of tables) stay NULL. */
IF OBJECT_ID(N'dbo.TableID_Setup', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.TableID_Setup', N'Origin') IS NULL
    ALTER TABLE dbo.TableID_Setup ADD Origin varchar(3) NULL;
GO
IF COL_LENGTH(N'dbo.TableID_Setup', N'Origin') IS NOT NULL
    EXEC sp_executesql N'UPDATE dbo.TableID_Setup
                            SET Origin = CASE WHEN VesselID = 0 THEN ''HQ'' ELSE ''VSL'' END
                          WHERE Origin IS NULL AND VesselID >= 0;';
GO

CREATE OR ALTER PROCEDURE dbo.Proc_NextID
(
    @TableName   varchar(50),
    @Origin      varchar(3),
    @VesselID    int,
    @Incrementor int,
    @NewTableID  bigint OUTPUT
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @MaxNumFromTable bigint, @LastNumFromTableID bigint, @TableID_SetupID bigint,
            @KeyColumn sysname, @Sql nvarchar(max), @Where nvarchar(400) = N' WHERE 1 = 1',
            @SetupVesselID int, @IsHQ bit, @ObjectId int;

    SET @TableName = LTRIM(RTRIM(@TableName));
    SET @Origin = UPPER(LTRIM(RTRIM(ISNULL(@Origin, ''))));
    SET @IsHQ = CASE WHEN @Origin = 'HQ' THEN 1 ELSE 0 END;
    IF @Incrementor IS NULL OR @Incrementor <= 0 SET @Incrementor = 1;

    -- ---- The table, and its primary key column ------------------------------------------------------
    SET @ObjectId = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U');
    IF @ObjectId IS NULL
    BEGIN
        RAISERROR('Proc_NextID: table dbo.%s does not exist.', 16, 1, @TableName);
        RETURN;
    END;

    SELECT @KeyColumn = c.name
      FROM sys.indexes i
      JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
      JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
     WHERE i.object_id = @ObjectId AND i.is_primary_key = 1
       AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id) = 1;

    IF @KeyColumn IS NULL AND COL_LENGTH(N'dbo.' + QUOTENAME(@TableName), @TableName + 'ID') IS NOT NULL
        SET @KeyColumn = @TableName + 'ID';

    IF @KeyColumn IS NULL
    BEGIN
        RAISERROR('Proc_NextID: cannot find a single-column primary key (or %sID) on dbo.%s.', 16, 1, @TableName, @TableName);
        RETURN;
    END;

    -- ---- Which rows count: HQ -> Origin only; VSL -> Origin and this vessel ---------------------------
    IF COL_LENGTH(N'dbo.' + QUOTENAME(@TableName), 'Origin') IS NOT NULL
        SET @Where += N' AND Origin = @Origin';
    IF @IsHQ = 0 AND COL_LENGTH(N'dbo.' + QUOTENAME(@TableName), 'VesselID') IS NOT NULL
        SET @Where += N' AND VesselID = @VesselID';

    SET @SetupVesselID = CASE WHEN @IsHQ = 1 THEN 0 ELSE @VesselID END;

    BEGIN TRANSACTION;

    -- One caller per table at a time, so two users saving together never get the same number.
    DECLARE @LockName nvarchar(255) = N'Proc_NextID:' + @TableName + N':' + CAST(@SetupVesselID AS nvarchar(20));
    DECLARE @Lock int;
    EXEC @Lock = sp_getapplock @Resource = @LockName, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
    IF @Lock < 0
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('Proc_NextID: timed out waiting for the next number of dbo.%s.', 16, 1, @TableName);
        RETURN;
    END;

    ---- Max primary key in that table (for this origin / vessel)
    SET @Sql = N'SELECT @Max = ISNULL(MAX(' + QUOTENAME(@KeyColumn) + N'), 0) FROM dbo.' + QUOTENAME(@TableName) + @Where + N';';
    EXEC sp_executesql @Sql,
         N'@Origin varchar(3), @VesselID int, @Max bigint OUTPUT',
         @Origin = @Origin, @VesselID = @VesselID, @Max = @MaxNumFromTable OUTPUT;

    ---- The counter row in TableID_Setup: TableName + Origin + VesselID (VesselID 0 for HQ)
    DECLARE @HasOrigin bit = CASE WHEN COL_LENGTH(N'dbo.TableID_Setup', N'Origin') IS NULL THEN 0 ELSE 1 END;
    DECLARE @SetupWhere nvarchar(200) = N' WHERE TableName = @Table AND VesselID = @Vessel'
                                       + CASE WHEN @HasOrigin = 1 THEN N' AND Origin = @Org' ELSE N'' END;
    DECLARE @Found int = 0;

    SET @Sql = N'SELECT @Found = COUNT(*), @Last = ISNULL(MAX(LastNumber), 0) FROM dbo.TableID_Setup WITH (UPDLOCK, HOLDLOCK)' + @SetupWhere + N';';
    EXEC sp_executesql @Sql,
         N'@Table varchar(50), @Vessel int, @Org varchar(3), @Found int OUTPUT, @Last bigint OUTPUT',
         @Table = @TableName, @Vessel = @SetupVesselID, @Org = @Origin, @Found = @Found OUTPUT, @Last = @LastNumFromTableID OUTPUT;

    -------------------------------------------------------------------------------------------------------
    IF @Found = 0
    BEGIN
        -- No counter yet: a vessel starts from its TableIDstart (V_Vessel on the ship); HQ from 0.
        SET @LastNumFromTableID = 0;
        IF @IsHQ = 0
        BEGIN
           IF COL_LENGTH('dbo.V_Vessel', 'TableIDstart') IS NOT NULL
                EXEC sp_executesql N'SELECT @Start = ISNULL(MAX(TableIDstart), 0) FROM dbo.V_Vessel WHERE VesselID = @VesselID;',
                     N'@VesselID int, @Start bigint OUTPUT', @VesselID = @VesselID, @Start = @LastNumFromTableID OUTPUT;
        END;
        SET @LastNumFromTableID = ISNULL(@LastNumFromTableID, 0);

        SET @TableID_SetupID = (SELECT ISNULL(MAX(TableID_SetupID), 0) FROM dbo.TableID_Setup WITH (UPDLOCK, HOLDLOCK)) + 1;
        -- (an older row for the same table and vessel without the origin would break the unique index)
        DELETE FROM dbo.TableID_Setup WHERE TableName = @TableName AND VesselID = @SetupVesselID;

        DECLARE @Cols nvarchar(200) = N'TableID_SetupID, TableName, VesselID, LastNumber'
                + CASE WHEN @HasOrigin = 1 THEN N', Origin' ELSE N'' END
                + CASE WHEN COL_LENGTH(N'dbo.TableID_Setup', N'IsForVsl') IS NOT NULL THEN N', IsForVsl' ELSE N'' END;
        DECLARE @Vals nvarchar(400) = N'@Id, @Table, @Vessel, @Last'
                + CASE WHEN @HasOrigin = 1 THEN N', @Org' ELSE N'' END
                -- Same IsForVsl as the table's other rows (0 when it has none).
                + CASE WHEN COL_LENGTH(N'dbo.TableID_Setup', N'IsForVsl') IS NOT NULL
                       THEN N', ISNULL((SELECT TOP (1) IsForVsl FROM dbo.TableID_Setup WHERE TableName = @Table AND IsForVsl IS NOT NULL), 0)'
                       ELSE N'' END;
        SET @Sql = N'INSERT INTO dbo.TableID_Setup (' + @Cols + N') SELECT ' + @Vals + N';';
        EXEC sp_executesql @Sql,
             N'@Id bigint, @Table varchar(50), @Vessel int, @Last bigint, @Org varchar(3)',
             @Id = @TableID_SetupID, @Table = @TableName, @Vessel = @SetupVesselID, @Last = @LastNumFromTableID, @Org = @Origin;
    END;
    -------------------------------------------------------------------------------------------------------

    ---- Next number: after the highest of the table's own rows and the last number handed out
    SET @NewTableID = CASE WHEN @MaxNumFromTable > @LastNumFromTableID THEN @MaxNumFromTable ELSE @LastNumFromTableID END + 1;

    ---- Never hand out a number another row already has (another origin or vessel, or a TableIDstart that is
    ---- missing or overlaps): carry on after the highest ID in the whole table instead.
    DECLARE @Clash int, @AllMax bigint, @To bigint;
    SET @To = @NewTableID + @Incrementor - 1;
    SET @Sql = N'SELECT @Clash = COUNT(*) FROM dbo.' + QUOTENAME(@TableName)
             + N' WHERE ' + QUOTENAME(@KeyColumn) + N' BETWEEN @From AND @To;'
             + N' SELECT @AllMax = ISNULL(MAX(' + QUOTENAME(@KeyColumn) + N'), 0) FROM dbo.' + QUOTENAME(@TableName) + N';';
    EXEC sp_executesql @Sql,
         N'@From bigint, @To bigint, @Clash int OUTPUT, @AllMax bigint OUTPUT',
         @From = @NewTableID, @To = @To, @Clash = @Clash OUTPUT, @AllMax = @AllMax OUTPUT;
    IF @Clash > 0 SET @NewTableID = @AllMax + 1;

    ---- Remember it in TableID_Setup (the whole block when @Incrementor > 1)
    SET @Sql = N'UPDATE dbo.TableID_Setup SET LastNumber = @Last' + @SetupWhere + N';';
    DECLARE @NewLast bigint = @NewTableID + @Incrementor - 1;
    EXEC sp_executesql @Sql,
         N'@Table varchar(50), @Vessel int, @Org varchar(3), @Last bigint',
         @Table = @TableName, @Vessel = @SetupVesselID, @Org = @Origin, @Last = @NewLast;

    COMMIT TRANSACTION;
END;
GO
