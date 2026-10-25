/* =====================================================================================================
   025_SyncOfficeFolders.sql  -  run on the OFFICE (HQ) database after 022_RenameFolderTable.sql. Safe to run again.

   Folders added at the office (dbo.OtherDocument_Folder) go to the vessel with the row sync, like the other
   synced tables: the trigger TR_OtherDocument_Folder_SyncRow writes a Sync_ChangeLog row, the ship fetches it
   with api/Sync/Changes and writes the folder into its own OtherDocument_Folder (same key, same values).

   Which folders go to a vessel:
     Vessel scope   VesselID = the vessel          -> that vessel
     Company scope  VesselID -1 (every vessel)     -> every active vessel
                    VesselID = a vessel            -> that vessel
     Department / Personal at the office (VesselID -2) stay at the office.
   A deleted folder (IsDeleted = 1) goes too, so the ship hides it as well.

   This script:
     1. makes sure OtherDocument_Folder is an active synced table (Sync_Table) and the old FLD_Folder entry is gone;
     2. (re)creates the row sync triggers, so TR_OtherDocument_Folder_SyncRow exists under the current name;
     3. queues every existing office folder for its vessel(s): folders made before the trigger existed, or while the
        table still had its old name, were never logged and so never reached the ship. A folder already waiting for a
        vessel is not queued twice. The vessels receive them with their next sync.

   @VesselID below: -1 = every active vessel, or one vessel's ID.
   ===================================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.OtherDocument_Folder', 'U') IS NULL
BEGIN
    RAISERROR('025: dbo.OtherDocument_Folder does not exist. Run 012_NewFeatures.sql and 022_RenameFolderTable.sql first.', 16, 1);
    SET NOEXEC ON;
END
IF OBJECT_ID('dbo.Sync_Table', 'U') IS NULL OR OBJECT_ID('dbo.Sync_LogChanges', 'P') IS NULL
BEGIN
    RAISERROR('025: the row sync is not installed. Run 010_Sync_ChangeLog.sql first.', 16, 1);
    SET NOEXEC ON;
END
GO

/* ---------------------------------------------------------------- 1. A synced table ---- */
DELETE FROM dbo.Sync_Table WHERE TableName = 'FLD_Folder';

IF EXISTS (SELECT 1 FROM dbo.Sync_Table WHERE TableName = 'OtherDocument_Folder')
    UPDATE dbo.Sync_Table SET IsActive = 1, AllVessels = 0, BlobColumn = NULL WHERE TableName = 'OtherDocument_Folder';
ELSE
    INSERT dbo.Sync_Table (TableName, SortOrder, BlobColumn, AllVessels, IsActive)
    VALUES ('OtherDocument_Folder', 600, NULL, 0, 1);

-- Waiting rows still under the old name follow the table.
UPDATE dbo.Sync_ChangeLog SET TableName = 'OtherDocument_Folder' WHERE TableName = 'FLD_Folder';

IF OBJECT_ID('dbo.TR_FLD_Folder_SyncRow', 'TR') IS NOT NULL
    DROP TRIGGER dbo.TR_FLD_Folder_SyncRow;
GO

/* ------------------------------------------------------------------- 2. The trigger ---- */
EXEC dbo.Sync_CreateRowTriggers;
GO

IF OBJECT_ID('dbo.TR_OtherDocument_Folder_SyncRow', 'TR') IS NULL
    RAISERROR('025: TR_OtherDocument_Folder_SyncRow was not created (see the messages above).', 16, 1);
GO

/* ------------------------------------------------- 3. Existing office folders to the vessels ---- */
DECLARE @VesselID int = -1;      -- -1 = every active vessel, or one vessel

;WITH folders AS
(
    SELECT f.OtherDocument_FolderID AS KeyValue, f.VesselID
      FROM dbo.OtherDocument_Folder f
     WHERE (f.VesselID = -1 OR f.VesselID >= 0)          -- not office-only (-2) or templates (-9)
       AND ISNULL(f.Scope, '') <> 'Personal'
),
targets AS
(
    SELECT v.VesselID, fo.KeyValue
      FROM folders fo
      JOIN dbo.V_Vessel v ON v.VesselID > 0 AND ISNULL(v.IsRetired, 0) = 0
                         AND (fo.VesselID IN (-1, 0) OR fo.VesselID = v.VesselID)   -- -1 (old 0) = every vessel
                         AND (@VesselID = -1 OR v.VesselID = @VesselID)
)
INSERT dbo.Sync_ChangeLog (VesselID, TableName, KeyValue, Action)
SELECT t.VesselID, 'OtherDocument_Folder', t.KeyValue, 'U'
  FROM targets t
 WHERE NOT EXISTS (SELECT 1 FROM dbo.Sync_ChangeLog w
                    WHERE w.VesselID = t.VesselID AND w.TableName = 'OtherDocument_Folder' AND w.KeyValue = t.KeyValue
                      AND w.SentUtc IS NULL AND w.DownloadedUtc IS NULL);

PRINT CAST(@@ROWCOUNT AS varchar(20)) + ' folder row(s) queued for the vessels.';

-- What is waiting now, per vessel.
SELECT w.VesselID, v.VslName AS Vessel, COUNT(*) AS FoldersWaiting
  FROM dbo.Sync_ChangeLog w
  LEFT JOIN dbo.V_Vessel v ON v.VesselID = w.VesselID
 WHERE w.TableName = 'OtherDocument_Folder' AND w.DownloadedUtc IS NULL
 GROUP BY w.VesselID, v.VslName
 ORDER BY v.VslName;
GO

SET NOEXEC OFF;
GO
PRINT '025_SyncOfficeFolders: office folders go to the vessels with the row sync.';
GO
