/* =====================================================================================================
   032_Sync_KeysWithoutIdentity.sql  -  run on the OFFICE (HQ) database.

   Sync_Log, Sync_Vessel and Sync_VesselKey now get their key from dbo.Proc_NextID, like the other tables
   (SyncEntities.cs: DatabaseGeneratedOption.None, SyncService: NextIdAsync). In the database the key column was
   still IDENTITY, so every insert failed with
       "Cannot insert explicit value for identity column in table 'Sync_Log' when IDENTITY_INSERT is set to OFF."

   For each of the three tables this script:
     - takes IDENTITY off the key column. SQL Server cannot ALTER that away, so a plain column of the same type
       takes the same values, the old column is dropped and the new one gets its name and the primary key (same
       constraint name). Other indexes on the key column are made again. All in one transaction per table:
       a failure leaves that table as it was.
     - adds Origin varchar(3) NOT NULL DEFAULT 'HQ' when the table has no Origin column (the entities write it).
   Rows and their keys are kept; Proc_NextID continues after the highest key (it reads MAX of the table).
   Safe to run again: a table already without IDENTITY is only reported.

   Not changed: Sync_ChangeLog keeps its IDENTITY key (the triggers insert without one).
   ===================================================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @tables TABLE (TableName sysname PRIMARY KEY);
INSERT @tables VALUES (N'Sync_Log'), (N'Sync_Vessel'), (N'Sync_VesselKey');

DECLARE @t sysname, @obj int, @col sysname, @type nvarchar(100), @pk sysname, @pkType nvarchar(60),
        @tmp sysname, @sql nvarchar(max), @indexes nvarchar(max), @dropIndexes nvarchar(max), @fk nvarchar(max);

DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @tables;
OPEN c; FETCH NEXT FROM c INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @obj = OBJECT_ID(N'dbo.' + QUOTENAME(@t), N'U');
    SELECT @col = NULL, @type = NULL, @pk = NULL, @pkType = NULL, @indexes = NULL, @dropIndexes = NULL, @fk = NULL;

    IF @obj IS NULL
        PRINT @t + ': not in this database - skipped.';
    ELSE
    BEGIN
        -- Origin, which the entities write (HQ for rows made at the office).
        IF COL_LENGTH(N'dbo.' + QUOTENAME(@t), N'Origin') IS NULL
        BEGIN
            SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@t) + N' ADD Origin varchar(3) NOT NULL CONSTRAINT '
                     + QUOTENAME(N'DF_' + @t + N'_Origin') + N' DEFAULT (''HQ'');';
            EXEC sp_executesql @sql;
            PRINT @t + ': Origin column added (HQ).';
        END;

        SELECT @col = c.name, @type = TYPE_NAME(c.system_type_id)
          FROM sys.columns c WHERE c.object_id = @obj AND c.is_identity = 1;

        IF @col IS NULL
            PRINT @t + ': key has no IDENTITY - nothing to do.';
        ELSE
        BEGIN
            SELECT @pk = k.name, @pkType = i.type_desc
              FROM sys.key_constraints k
              JOIN sys.indexes i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
             WHERE k.parent_object_id = @obj AND k.type = 'PK';

            -- A foreign key pointing at this table would have to be dropped and made again: not done here.
            SELECT @fk = STRING_AGG(CAST(OBJECT_NAME(f.parent_object_id) + N'.' + f.name AS nvarchar(max)), N', ')
              FROM sys.foreign_keys f WHERE f.referenced_object_id = @obj;

            IF @fk IS NOT NULL
                PRINT @t + ': NOT changed - foreign keys refer to it (' + @fk + '). Drop them, run this again, add them back.';
            ELSE IF @pk IS NULL
                PRINT @t + ': NOT changed - it has no primary key.';
            ELSE
            BEGIN
                -- Other indexes that use the key column: dropped first and made again on the new column.
                -- (Each index's statement is built first; STRING_AGG cannot hold a subquery.)
                ;WITH ix AS
                (
                    SELECT i.name,
                           Definition = N'CREATE ' + CASE WHEN i.is_unique = 1 THEN N'UNIQUE ' ELSE N'' END
                                      + CASE WHEN i.type = 1 THEN N'CLUSTERED' ELSE N'NONCLUSTERED' END
                                      + N' INDEX ' + QUOTENAME(i.name) + N' ON dbo.' + QUOTENAME(@t) + N' (' + k.Cols + N')'
                                      + ISNULL(N' INCLUDE (' + inc.Cols + N')', N'')
                                      + ISNULL(N' WHERE ' + i.filter_definition, N'') + N';'
                      FROM sys.indexes i
                     CROSS APPLY (SELECT STRING_AGG(CAST(QUOTENAME(ic.name) + CASE WHEN x.is_descending_key = 1 THEN N' DESC' ELSE N'' END AS nvarchar(max)), N', ')
                                         WITHIN GROUP (ORDER BY x.key_ordinal) AS Cols
                                    FROM sys.index_columns x
                                    JOIN sys.columns ic ON ic.object_id = x.object_id AND ic.column_id = x.column_id
                                   WHERE x.object_id = i.object_id AND x.index_id = i.index_id AND x.is_included_column = 0) k
                     OUTER APPLY (SELECT STRING_AGG(CAST(QUOTENAME(ic.name) AS nvarchar(max)), N', ') AS Cols
                                    FROM sys.index_columns x
                                    JOIN sys.columns ic ON ic.object_id = x.object_id AND ic.column_id = x.column_id
                                   WHERE x.object_id = i.object_id AND x.index_id = i.index_id AND x.is_included_column = 1) inc
                     WHERE i.object_id = @obj AND i.is_primary_key = 0 AND i.type > 0
                       AND EXISTS (SELECT 1 FROM sys.index_columns x
                                     JOIN sys.columns ic ON ic.object_id = x.object_id AND ic.column_id = x.column_id
                                    WHERE x.object_id = i.object_id AND x.index_id = i.index_id AND ic.name = @col)
                )
                SELECT @dropIndexes = STRING_AGG(CAST(N'DROP INDEX ' + QUOTENAME(name) + N' ON dbo.' + QUOTENAME(@t) + N';' AS nvarchar(max)), NCHAR(10)),
                       @indexes     = STRING_AGG(CAST(Definition AS nvarchar(max)), NCHAR(10))
                  FROM ix;

                SET @tmp = @col + N'_NoIdentity';
                SET @sql = N'
ALTER TABLE dbo.' + QUOTENAME(@t) + N' ADD ' + QUOTENAME(@tmp) + N' ' + @type + N' NULL;';
                -- (a separate batch: the new column must exist before the UPDATE is compiled)
                BEGIN TRAN;
                    EXEC sp_executesql @sql;

                    SET @sql = N'UPDATE dbo.' + QUOTENAME(@t) + N' SET ' + QUOTENAME(@tmp) + N' = ' + QUOTENAME(@col) + N';
ALTER TABLE dbo.' + QUOTENAME(@t) + N' ALTER COLUMN ' + QUOTENAME(@tmp) + N' ' + @type + N' NOT NULL;'
                        + ISNULL(NCHAR(10) + @dropIndexes, N'') + N'
ALTER TABLE dbo.' + QUOTENAME(@t) + N' DROP CONSTRAINT ' + QUOTENAME(@pk) + N';
ALTER TABLE dbo.' + QUOTENAME(@t) + N' DROP COLUMN ' + QUOTENAME(@col) + N';';
                    EXEC sp_executesql @sql;

                    SET @sql = N'dbo.' + QUOTENAME(@t) + N'.' + QUOTENAME(@tmp);
                    EXEC sp_rename @sql, @col, N'COLUMN';

                    SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@t) + N' ADD CONSTRAINT ' + QUOTENAME(@pk) + N' PRIMARY KEY '
                             + CASE WHEN @pkType = N'CLUSTERED' THEN N'CLUSTERED' ELSE N'NONCLUSTERED' END
                             + N' (' + QUOTENAME(@col) + N');' + ISNULL(NCHAR(10) + @indexes, N'');
                    EXEC sp_executesql @sql;
                COMMIT;

                PRINT @t + ': IDENTITY taken off ' + @col + ' (rows and keys kept; new keys from Proc_NextID).';
            END;
        END;
    END;

    FETCH NEXT FROM c INTO @t;
END
CLOSE c; DEALLOCATE c;

-- Check: none of the three should be listed.
SELECT OBJECT_NAME(c.object_id) AS TableStillWithIdentity, c.name AS KeyColumn
  FROM sys.columns c
 WHERE c.is_identity = 1 AND c.object_id IN (OBJECT_ID(N'dbo.Sync_Log'), OBJECT_ID(N'dbo.Sync_Vessel'), OBJECT_ID(N'dbo.Sync_VesselKey'));
GO
