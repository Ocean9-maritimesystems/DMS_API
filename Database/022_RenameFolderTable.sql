/* =====================================================================================================
   022_RenameFolderTable.sql  -  run on the OFFICE (HQ) database after 005-021. Safe to run more than once.

   The folders of Other Documents get the Other Document names the API already uses:

       dbo.FLD_Folder                       ->  dbo.OtherDocument_Folder
       FLD_Folder.FLD_FolderID              ->  OtherDocument_Folder.OtherDocument_FolderID
       OtherDocumentMaster.FLD_FolderID     ->  OtherDocumentMaster.OtherDocument_FolderID

   and everything that names the table follows: Sync_Table, Sync_ChangeLog, TableID_Setup, the row sync trigger
   (TR_FLD_Folder_SyncRow -> TR_OtherDocument_Folder_SyncRow), the primary key, the defaults and the folder index.
   Data and IDs are kept as they are. The settings FLD_MaxFileSizeMB / FLD_MaxFiles keep their names.

   If both the old and the new table exist (a database that was half moved), the old one is dropped when it is
   empty; when both have rows the script stops and changes nothing.

   Update every ship to the vessel build that uses OtherDocument_Folder at the same time: an older ship build
   still sends and expects FLD_Folder, and its folder changes are refused until it is updated.
   ===================================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* ------------------------------------------------------------------------------------- Checks ---- */
IF OBJECT_ID('dbo.FLD_Folder', 'U') IS NOT NULL AND OBJECT_ID('dbo.OtherDocument_Folder', 'U') IS NOT NULL
BEGIN
    DECLARE @both int;
    EXEC sp_executesql N'SELECT @n = CASE WHEN EXISTS (SELECT 1 FROM dbo.FLD_Folder)
                                           AND EXISTS (SELECT 1 FROM dbo.OtherDocument_Folder) THEN 1 ELSE 0 END;',
         N'@n int OUTPUT', @n = @both OUTPUT;
    IF @both = 1
    BEGIN
        RAISERROR('022: both dbo.FLD_Folder and dbo.OtherDocument_Folder hold folders. Merge them by hand first; nothing was changed.', 16, 1);
        SET NOEXEC ON;   -- the rest of the script is skipped
    END
END
GO

BEGIN TRANSACTION;

/* ----------------------------------------------------------------------------------- The table ---- */
IF OBJECT_ID('dbo.FLD_Folder', 'U') IS NOT NULL AND OBJECT_ID('dbo.OtherDocument_Folder', 'U') IS NOT NULL
BEGIN
    -- One of the two is empty (checked above): keep the one with the folders.
    IF EXISTS (SELECT 1 FROM dbo.FLD_Folder)
    BEGIN
        DROP TABLE dbo.OtherDocument_Folder;
        PRINT 'Dropped the empty dbo.OtherDocument_Folder (the folders are in dbo.FLD_Folder).';
    END
    ELSE
    BEGIN
        DROP TABLE dbo.FLD_Folder;
        PRINT 'Dropped the empty dbo.FLD_Folder.';
    END
END

IF OBJECT_ID('dbo.TR_FLD_Folder_SyncRow', 'TR') IS NOT NULL
    DROP TRIGGER dbo.TR_FLD_Folder_SyncRow;   -- it logs under the old name; re-created below

IF OBJECT_ID('dbo.FLD_Folder', 'U') IS NOT NULL
BEGIN
    EXEC sp_rename 'dbo.FLD_Folder', 'OtherDocument_Folder';
    PRINT 'Renamed dbo.FLD_Folder to dbo.OtherDocument_Folder.';
END

IF COL_LENGTH('dbo.OtherDocument_Folder', 'FLD_FolderID') IS NOT NULL
   AND COL_LENGTH('dbo.OtherDocument_Folder', 'OtherDocument_FolderID') IS NULL
    EXEC sp_rename 'dbo.OtherDocument_Folder.FLD_FolderID', 'OtherDocument_FolderID', 'COLUMN';

IF OBJECT_ID('dbo.PK_FLD_Folder', 'PK') IS NOT NULL AND OBJECT_ID('dbo.PK_OtherDocument_Folder') IS NULL
    EXEC sp_rename 'dbo.PK_FLD_Folder', 'PK_OtherDocument_Folder', 'OBJECT';
IF OBJECT_ID('dbo.DF_FLD_Folder_IsDeleted', 'D') IS NOT NULL AND OBJECT_ID('dbo.DF_OtherDocument_Folder_IsDeleted') IS NULL
    EXEC sp_rename 'dbo.DF_FLD_Folder_IsDeleted', 'DF_OtherDocument_Folder_IsDeleted', 'OBJECT';
IF OBJECT_ID('dbo.DF_FLD_Folder_IsPendingUpload', 'D') IS NOT NULL AND OBJECT_ID('dbo.DF_OtherDocument_Folder_IsPendingUpload') IS NULL
    EXEC sp_rename 'dbo.DF_FLD_Folder_IsPendingUpload', 'DF_OtherDocument_Folder_IsPendingUpload', 'OBJECT';

/* ------------------------------------------------------- The folder column on OtherDocumentMaster ---- */
-- The filtered index names the column in its filter: dropped first, re-created on the new column below.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OtherDocumentMaster_Folder' AND object_id = OBJECT_ID('dbo.OtherDocumentMaster'))
   AND COL_LENGTH('dbo.OtherDocumentMaster', 'FLD_FolderID') IS NOT NULL
    DROP INDEX IX_OtherDocumentMaster_Folder ON dbo.OtherDocumentMaster;

IF COL_LENGTH('dbo.OtherDocumentMaster', 'FLD_FolderID') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.OtherDocumentMaster', 'OtherDocument_FolderID') IS NULL
        EXEC sp_rename 'dbo.OtherDocumentMaster.FLD_FolderID', 'OtherDocument_FolderID', 'COLUMN';
    ELSE
    BEGIN
        -- Both columns: keep every folder that is only in the old one, then drop it.
        EXEC sp_executesql N'UPDATE dbo.OtherDocumentMaster SET OtherDocument_FolderID = FLD_FolderID
                              WHERE OtherDocument_FolderID IS NULL AND FLD_FolderID IS NOT NULL;';
        EXEC sp_executesql N'ALTER TABLE dbo.OtherDocumentMaster DROP COLUMN FLD_FolderID;';
    END
    PRINT 'OtherDocumentMaster.FLD_FolderID is now OtherDocument_FolderID.';
END

IF COL_LENGTH('dbo.OtherDocumentMaster', 'OtherDocument_FolderID') IS NULL
    ALTER TABLE dbo.OtherDocumentMaster ADD OtherDocument_FolderID bigint NULL;

/* -------------------------------------------------------------------- Sync and ID bookkeeping ---- */
IF OBJECT_ID('dbo.Sync_Table', 'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Sync_Table WHERE TableName = 'OtherDocument_Folder')
        DELETE FROM dbo.Sync_Table WHERE TableName = 'FLD_Folder';
    ELSE
        UPDATE dbo.Sync_Table SET TableName = 'OtherDocument_Folder' WHERE TableName = 'FLD_Folder';
END

IF OBJECT_ID('dbo.Sync_ChangeLog', 'U') IS NOT NULL
    UPDATE dbo.Sync_ChangeLog SET TableName = 'OtherDocument_Folder' WHERE TableName = 'FLD_Folder';

IF OBJECT_ID('dbo.TableID_Setup', 'U') IS NOT NULL
BEGIN
    -- Keep the highest number handed out for each vessel under the new name.
    UPDATE n SET LastNumber = o.LastNumber
      FROM dbo.TableID_Setup n
      JOIN dbo.TableID_Setup o ON o.TableName = 'FLD_Folder' AND o.VesselID = n.VesselID
     WHERE n.TableName = 'OtherDocument_Folder' AND o.LastNumber > n.LastNumber;
    DELETE o FROM dbo.TableID_Setup o
     WHERE o.TableName = 'FLD_Folder'
       AND EXISTS (SELECT 1 FROM dbo.TableID_Setup n WHERE n.TableName = 'OtherDocument_Folder' AND n.VesselID = o.VesselID);
    UPDATE dbo.TableID_Setup SET TableName = 'OtherDocument_Folder' WHERE TableName = 'FLD_Folder';
END

COMMIT TRANSACTION;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OtherDocumentMaster_Folder' AND object_id = OBJECT_ID('dbo.OtherDocumentMaster'))
    CREATE INDEX IX_OtherDocumentMaster_Folder ON dbo.OtherDocumentMaster (OtherDocument_FolderID) WHERE OtherDocument_FolderID IS NOT NULL;
GO

-- The row sync trigger under the new name (TR_OtherDocument_Folder_SyncRow).
IF OBJECT_ID('dbo.Sync_CreateRowTriggers', 'P') IS NOT NULL
    EXEC dbo.Sync_CreateRowTriggers;
GO

PRINT '022_RenameFolderTable: folders are in dbo.OtherDocument_Folder, documents point to them with OtherDocument_FolderID.';
GO
SET NOEXEC OFF;
GO
