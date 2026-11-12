/* =====================================================================================================
   034_SchemaVersion.sql  -  run on the OFFICE (HQ) database and on every SHIP database.

   dbo.SchemaVersion: one row per numbered script that has been run on this database (ScriptName, when, by whom).
   From now on every script records itself at its end (029 - 034 do already), and the office API logs at start
   which of the scripts its build needs are not recorded, so a database that missed one is seen at once instead of
   failing in odd places (the way a database made from an older 000_Full_HQ_DB.sql does).

   Safe to run again.

   Scripts run BEFORE this one were not recorded. After running this, record the ones this database has had, e.g.
       EXEC dbo.SchemaVersion_Record '029_HQ_Sync_CreateRowTriggers';
       EXEC dbo.SchemaVersion_Record '030_Ship_CreateLogTriggers';
       EXEC dbo.SchemaVersion_Record '031_Ship_SyncTables';
       EXEC dbo.SchemaVersion_Record '032_Sync_KeysWithoutIdentity';
       EXEC dbo.SchemaVersion_Record '033_FileTable_Indexes';
   (or simply run those scripts again: each is safe to repeat and records itself).
   ===================================================================================================== */
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SchemaVersion
    (
        ScriptName varchar(100)  NOT NULL CONSTRAINT PK_SchemaVersion PRIMARY KEY,
        AppliedUtc datetime2(0)  NOT NULL CONSTRAINT DF_SchemaVersion_AppliedUtc DEFAULT (SYSUTCDATETIME()),
        AppliedBy  nvarchar(128) NOT NULL CONSTRAINT DF_SchemaVersion_AppliedBy DEFAULT (SUSER_SNAME())
    );
    PRINT 'dbo.SchemaVersion created.';
END;
GO

CREATE OR ALTER PROCEDURE dbo.SchemaVersion_Record
    @ScriptName varchar(100)   -- the script's file name without .sql, e.g. '033_FileTable_Indexes'
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.SchemaVersion SET AppliedUtc = SYSUTCDATETIME(), AppliedBy = SUSER_SNAME() WHERE ScriptName = @ScriptName;
    IF @@ROWCOUNT = 0 INSERT dbo.SchemaVersion (ScriptName) VALUES (@ScriptName);
END;
GO

EXEC dbo.SchemaVersion_Record '034_SchemaVersion';
GO

-- What this database has had.
SELECT ScriptName, AppliedUtc, AppliedBy FROM dbo.SchemaVersion ORDER BY ScriptName;
GO
