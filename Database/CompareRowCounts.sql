/* CompareRowCounts.sql - run on the OFFICE (HQ) database. Read only.

   Counts the rows of every table that exists in both the office database and the vessel database, and lists the
   tables whose counts do not match.

   The office holds every vessel's rows, so for a table with a VesselID column it counts only the rows that belong to
   this vessel, the same way the sync decides: VesselID = the vessel, or -1 / 0 (every vessel); rows with
   IsForOfficeOnly = 1 are left out. Tables marked AllVessels in Sync_Table are counted whole.

   Set @VesselDb (the vessel database on this server) and @VesselID, then run.
   @OnlyMismatches = 0 lists every table. */

SET NOCOUNT ON;

DECLARE @VesselDb       sysname = N'DMS_Vessel';
DECLARE @VesselID       int     = 101;
DECLARE @OnlyMismatches bit     = 1;

IF DB_ID(@VesselDb) IS NULL
BEGIN
    RAISERROR('Database %s is not on this server.', 16, 1, @VesselDb);
    RETURN;
END

-- The tables both databases have (names compared in one collation: the two databases use different ones).
DECLARE @tables TABLE (TableName sysname COLLATE DATABASE_DEFAULT PRIMARY KEY);
DECLARE @sql nvarchar(max) = N'
SELECT h.name
  FROM sys.tables h
  JOIN ' + QUOTENAME(@VesselDb) + N'.sys.tables v
    ON v.name COLLATE DATABASE_DEFAULT = h.name COLLATE DATABASE_DEFAULT
   AND SCHEMA_NAME(h.schema_id) = ''dbo''
   AND v.schema_id = 1;';
INSERT @tables (TableName) EXEC sp_executesql @sql;

DECLARE @result TABLE
(
    TableName     sysname COLLATE DATABASE_DEFAULT,
    Synced        varchar(3),
    HQ_AllRows    bigint,
    HQ_ForVessel  bigint,
    VSL_Rows      bigint,
    VSL_Pending   bigint,      -- rows made on board and not at the office yet (IsPendingUpload = 1)
    Difference    bigint,
    Note          nvarchar(200)
);

DECLARE @t sysname, @hasVessel bit, @officeOnly bit, @all bit, @synced bit, @vslPendingCol bit,
        @hqAll bigint, @hqFor bigint, @vsl bigint, @vslPending bigint, @where nvarchar(400);

DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @tables ORDER BY TableName;
OPEN c; FETCH NEXT FROM c INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @hasVessel  = CASE WHEN COL_LENGTH(N'dbo.' + QUOTENAME(@t), 'VesselID') IS NULL THEN 0 ELSE 1 END;
    SET @officeOnly = CASE WHEN COL_LENGTH(N'dbo.' + QUOTENAME(@t), 'IsForOfficeOnly') IS NULL THEN 0 ELSE 1 END;
    SELECT @synced = CASE WHEN EXISTS (SELECT 1 FROM dbo.Sync_Table s
                                        WHERE s.TableName COLLATE DATABASE_DEFAULT = @t AND s.IsActive = 1) THEN 1 ELSE 0 END,
           @all    = ISNULL((SELECT TOP 1 s.AllVessels FROM dbo.Sync_Table s WHERE s.TableName COLLATE DATABASE_DEFAULT = @t), 0);

    -- Office: all rows, and the rows this vessel should hold.
    SET @where = N'1 = 1';
    IF @hasVessel = 1 AND @all = 0 SET @where += N' AND (VesselID = @v OR VesselID IN (-1, 0))';
    IF @officeOnly = 1             SET @where += N' AND ISNULL(IsForOfficeOnly, 0) <> 1';

    SET @sql = N'SELECT @all = COUNT_BIG(*), @for = SUM(CASE WHEN ' + @where + N' THEN 1 ELSE 0 END) FROM dbo.' + QUOTENAME(@t) + N';';
    EXEC sp_executesql @sql, N'@v int, @all bigint OUTPUT, @for bigint OUTPUT', @v = @VesselID, @all = @hqAll OUTPUT, @for = @hqFor OUTPUT;

    -- Vessel: all rows, and those still waiting to go to the office.
    SET @sql = N'SELECT @n = CASE WHEN COL_LENGTH(N''' + REPLACE(QUOTENAME(@VesselDb), '''', '''''') + N'.dbo.' + REPLACE(QUOTENAME(@t), '''', '''''')
             + N''', ''IsPendingUpload'') IS NULL THEN 0 ELSE 1 END;';
    EXEC sp_executesql @sql, N'@n bit OUTPUT', @n = @vslPendingCol OUTPUT;

    SET @sql = N'SELECT @n = COUNT_BIG(*), @p = '
             + CASE WHEN @vslPendingCol = 1 THEN N'SUM(CASE WHEN IsPendingUpload = 1 THEN 1 ELSE 0 END)' ELSE N'0' END
             + N' FROM ' + QUOTENAME(@VesselDb) + N'.dbo.' + QUOTENAME(@t) + N';';
    EXEC sp_executesql @sql, N'@n bigint OUTPUT, @p bigint OUTPUT', @n = @vsl OUTPUT, @p = @vslPending OUTPUT;

    INSERT @result (TableName, Synced, HQ_AllRows, HQ_ForVessel, VSL_Rows, VSL_Pending, Difference, Note)
    VALUES (@t,
            CASE WHEN @synced = 1 THEN 'Yes' ELSE 'No' END,
            @hqAll, ISNULL(@hqFor, 0), @vsl, ISNULL(@vslPending, 0),
            -- what the vessel has beyond the office's rows for it, not counting rows still waiting to go up
            (@vsl - ISNULL(@vslPending, 0)) - ISNULL(@hqFor, 0),
            CASE WHEN @synced = 0 THEN N'Not in Sync_Table: not synced row by row'
                 WHEN @hasVessel = 0 OR @all = 1 THEN N'Counted whole (no vessel filter)'
                 ELSE N'Office counted for VesselID ' + CAST(@VesselID AS nvarchar(10)) + N', -1 and 0' END);

    FETCH NEXT FROM c INTO @t;
END
CLOSE c; DEALLOCATE c;

SELECT TableName, Synced, HQ_AllRows, HQ_ForVessel, VSL_Rows, VSL_Pending, Difference,
       CASE WHEN Difference > 0 THEN 'More on the vessel'
            WHEN Difference < 0 THEN 'Missing on the vessel'
            ELSE 'Match' END AS Result,
       Note
  FROM @result
 WHERE @OnlyMismatches = 0 OR Difference <> 0
 ORDER BY CASE WHEN Synced = 'Yes' THEN 0 ELSE 1 END, ABS(Difference) DESC, TableName;
