
/* ------------------------------------------------------------------ 1. Ship_ApplyRowChange (fixed) ---- */
CREATE OR ALTER PROCEDURE dbo.Ship_ApplyRowChange
    @TableName  sysname,
    @KeyColumn  sysname,
    @Key        bigint,
    @Action     char(1),                 -- U = insert or update, D = remove
    @Row        nvarchar(max) = NULL,    -- the record as the office holds it (JSON object, column -> value)
    @BlobColumn sysname = NULL,          -- file contents: never taken from the row (downloaded by the sync)
    @Result     varchar(20) OUTPUT       -- Written | Removed | Unchanged | Pending | Kept | NoTable | NoKey
AS
BEGIN
    /* Office record -> ship table of the same name, same key, every column both have.
         - The file contents are never written (the sync downloads them).
         - Columns the ship table does not have are ignored; text longer than the column is cut.
         - A record changed on board that is not at the office yet (waiting in Ship_ChangeLog) is left alone.
         - A file row whose BlobSize / AddedOn changed at the office gets its contents cleared, so the next
           download step of the sync fetches the new file.
         - A folder made on board (Origin VSL) is never removed: the office deletes folders with IsDeleted. */
    SET NOCOUNT ON;
    DECLARE @obj int = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U');
    IF @obj IS NULL BEGIN SET @Result = 'NoTable'; RETURN; END;

    DECLARE @keyName sysname, @keyIdentity bit;
    SELECT @keyName = c.name, @keyIdentity = c.is_identity
      FROM sys.columns c
     WHERE c.object_id = @obj AND c.name COLLATE DATABASE_DEFAULT = @KeyColumn COLLATE DATABASE_DEFAULT;
    IF @keyName IS NULL BEGIN SET @Result = 'NoKey'; RETURN; END;

    DECLARE @t nvarchar(300) = N'dbo.' + QUOTENAME(@TableName), @k nvarchar(300) = QUOTENAME(@keyName),
            @sql nvarchar(max), @n int;

    -- Changed on board and not at the office yet: never overwritten or removed from here.
    IF OBJECT_ID(N'dbo.Ship_ChangeLog', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM dbo.Ship_ChangeLog
                    WHERE TableName COLLATE DATABASE_DEFAULT = @TableName COLLATE DATABASE_DEFAULT
                      AND KeyValue = @Key AND ConfirmedUtc IS NULL)
    BEGIN SET @Result = 'Pending'; RETURN; END;

    /* ---- Remove ---- */
    IF @Action = 'D' OR @Row IS NULL
    BEGIN
        IF @TableName = N'OtherDocument_Folder' AND COL_LENGTH(@t, 'Origin') IS NOT NULL
        BEGIN
            SET @sql = N'SELECT @n = COUNT(*) FROM ' + @t + N' WHERE ' + @k + N' = @Key AND Origin = ''VSL'';';
            EXEC sp_executesql @sql, N'@Key bigint, @n int OUTPUT', @Key = @Key, @n = @n OUTPUT;
            IF @n > 0 BEGIN SET @Result = 'Kept'; RETURN; END;
        END;
        SET @sql = N'DELETE FROM ' + @t + N' WHERE ' + @k + N' = @Key;
                     SET @r = CASE WHEN @@ROWCOUNT > 0 THEN ''Removed'' ELSE ''Unchanged'' END;';
        EXEC sp_executesql @sql, N'@Key bigint, @r varchar(20) OUTPUT', @Key = @Key, @r = @Result OUTPUT;
        RETURN;
    END;

    /* ---- Insert or update: the columns the row and the table both have ---- */
    DECLARE @cols TABLE
    (
        ColumnId    int PRIMARY KEY,
        ColumnName  sysname COLLATE DATABASE_DEFAULT,
        JsonName    nvarchar(4000) COLLATE DATABASE_DEFAULT,
        TypeName    sysname COLLATE DATABASE_DEFAULT,
        MaxLength   smallint,
        [Precision] tinyint,
        Scale       tinyint,
        IsIdentity  bit
    );
    INSERT @cols (ColumnId, ColumnName, JsonName, TypeName, MaxLength, [Precision], Scale, IsIdentity)
    SELECT c.column_id, c.name, j.[key], TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.is_identity
      FROM sys.columns c
      JOIN OPENJSON(@Row) j ON j.[key] COLLATE DATABASE_DEFAULT = c.name COLLATE DATABASE_DEFAULT
     WHERE c.object_id = @obj
       AND c.is_computed = 0
       AND TYPE_NAME(c.system_type_id) NOT IN (N'timestamp', N'rowversion')
       AND c.name COLLATE DATABASE_DEFAULT <> @keyName COLLATE DATABASE_DEFAULT
       AND (@BlobColumn IS NULL OR c.name COLLATE DATABASE_DEFAULT <> @BlobColumn COLLATE DATABASE_DEFAULT);

    -- Each value arrives as JSON text (column cN of the OPENJSON source); turned into the column's type here.
    DECLARE @with nvarchar(max), @select nvarchar(max), @set nvarchar(max), @ins nvarchar(max), @insv nvarchar(max);
    ;WITH x AS
    (
        SELECT ColumnId, ColumnName, JsonName, IsIdentity, TypeName, MaxLength,
               cn = N'c' + CAST(ColumnId AS nvarchar(10)),
               v  = N'j.' + QUOTENAME(N'c' + CAST(ColumnId AS nvarchar(10))),
               full_type = TypeName + CASE
                   WHEN TypeName IN (N'decimal', N'numeric') THEN N'(' + CAST([Precision] AS nvarchar(5)) + N',' + CAST(Scale AS nvarchar(5)) + N')'
                   WHEN TypeName IN (N'datetime2', N'time', N'datetimeoffset') THEN N'(' + CAST(Scale AS nvarchar(5)) + N')'
                   ELSE N'' END
          FROM @cols
    ),
    e AS
    (
        SELECT x.*, expr = CASE
            WHEN TypeName IN (N'varchar', N'char') AND MaxLength > 0 THEN N'LEFT(' + v + N', ' + CAST(MaxLength AS nvarchar(10)) + N')'
            WHEN TypeName IN (N'nvarchar', N'nchar') AND MaxLength > 0 THEN N'LEFT(' + v + N', ' + CAST(MaxLength / 2 AS nvarchar(10)) + N')'
            WHEN TypeName IN (N'varchar', N'char', N'nvarchar', N'nchar', N'text', N'ntext') THEN v
            WHEN TypeName IN (N'bit', N'tinyint', N'smallint', N'int', N'bigint')
                THEN N'CASE LOWER(' + v + N') WHEN ''true'' THEN 1 WHEN ''false'' THEN 0 ELSE TRY_CAST(' + v + N' AS ' + full_type + N') END'
            WHEN TypeName IN (N'datetime', N'smalldatetime', N'date', N'datetime2')
                THEN N'CAST(TRY_CONVERT(datetime2(7), CASE WHEN ' + v + N' LIKE ''%Z'' THEN LEFT(' + v + N', LEN(' + v + N') - 1) ELSE ' + v + N' END, 126) AS ' + full_type + N')'
            WHEN TypeName = N'datetimeoffset' THEN N'TRY_CONVERT(' + full_type + N', ' + v + N', 127)'
            WHEN TypeName IN (N'varbinary', N'binary', N'image')
                THEN N'CAST(N'''' AS xml).value(''xs:base64Binary(sql:column("j.' + cn + N'"))'', ''varbinary(max)'')'
            WHEN TypeName IN (N'xml', N'sql_variant', N'hierarchyid', N'geography', N'geometry') THEN v
            ELSE N'TRY_CAST(' + v + N' AS ' + full_type + N')' END
          FROM x
    )
    SELECT @with   = STRING_AGG(CAST(QUOTENAME(cn) + N' nvarchar(max) ''$."' + REPLACE(JsonName, N'''', N'''''') + N'"''' AS nvarchar(max)), N', ')
                         WITHIN GROUP (ORDER BY ColumnId),
           @select = STRING_AGG(CAST(expr + N' AS ' + QUOTENAME(ColumnName) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY ColumnId),
           @set    = STRING_AGG(CASE WHEN IsIdentity = 0 THEN CAST(N't.' + QUOTENAME(ColumnName) + N' = s.' + QUOTENAME(ColumnName) AS nvarchar(max)) END, N', ')
                         WITHIN GROUP (ORDER BY ColumnId),
           @ins    = STRING_AGG(CASE WHEN IsIdentity = 0 THEN CAST(N', ' + QUOTENAME(ColumnName) AS nvarchar(max)) END, N'') WITHIN GROUP (ORDER BY ColumnId),
           @insv   = STRING_AGG(CASE WHEN IsIdentity = 0 THEN CAST(N', s.' + QUOTENAME(ColumnName) AS nvarchar(max)) END, N'') WITHIN GROUP (ORDER BY ColumnId)
      FROM e;

    -- A file whose size or date changed at the office: clear the contents on board (downloaded again).
    DECLARE @blob sysname;
    SELECT @blob = c.name FROM sys.columns c
     WHERE c.object_id = @obj AND @BlobColumn IS NOT NULL
       AND c.name COLLATE DATABASE_DEFAULT = @BlobColumn COLLATE DATABASE_DEFAULT;
    IF @blob IS NOT NULL AND EXISTS (SELECT 1 FROM @cols WHERE ColumnName IN (N'BlobSize', N'AddedOn'))
    BEGIN
        DECLARE @changed nvarchar(max) = STUFF(
            CASE WHEN EXISTS (SELECT 1 FROM @cols WHERE ColumnName = N'BlobSize')
                 THEN N' OR ISNULL(CAST(t.[BlobSize] AS bigint), -1) <> ISNULL(CAST(s.[BlobSize] AS bigint), -1)' ELSE N'' END +
            CASE WHEN EXISTS (SELECT 1 FROM @cols WHERE ColumnName = N'AddedOn')
                 THEN N' OR ISNULL(CAST(t.[AddedOn] AS datetime2), ''19000101'') <> ISNULL(CAST(s.[AddedOn] AS datetime2), ''19000101'')' ELSE N'' END,
            1, 4, N'');
        SET @set = ISNULL(@set + N', ', N'') + N't.' + QUOTENAME(@blob) + N' = CASE WHEN ' + @changed + N' THEN NULL ELSE t.' + QUOTENAME(@blob) + N' END';
    END;

    DECLARE @source nvarchar(max) = CASE WHEN @select IS NULL THEN N'SELECT 1 AS [_]'
                                         ELSE N'SELECT ' + @select + N' FROM OPENJSON(@Row) WITH (' + @with + N') j' END;

    SET @sql = N'DECLARE @w int;' +
        CASE WHEN @set IS NULL
             THEN N' SELECT @w = COUNT(*) FROM ' + @t + N' WHERE ' + @k + N' = @Key;'
             ELSE N' ;WITH s AS (' + @source + N') UPDATE t SET ' + @set + N' FROM ' + @t + N' t CROSS JOIN s WHERE t.' + @k + N' = @Key;
                     SET @w = @@ROWCOUNT;' END + N'
        IF @w = 0
        BEGIN' +
            CASE WHEN @keyIdentity = 1 THEN N' SET IDENTITY_INSERT ' + @t + N' ON;' ELSE N'' END + N'
            ; WITH s AS (' + @source + N')
            INSERT ' + @t + N' (' + @k + ISNULL(@ins, N'') + N') SELECT @Key' + ISNULL(@insv, N'') + N' FROM s;' +
            CASE WHEN @keyIdentity = 1 THEN N' SET IDENTITY_INSERT ' + @t + N' OFF;' ELSE N'' END + N'
        END;
        SET @r = ''Written'';';
    EXEC sp_executesql @sql, N'@Key bigint, @Row nvarchar(max), @r varchar(20) OUTPUT', @Key = @Key, @Row = @Row, @r = @Result OUTPUT;
END
GO

