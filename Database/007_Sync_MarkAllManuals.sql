/* =====================================================================================
   007_Sync_MarkAllManuals.sql  -  run on the OFFICE (HQ) database, after 006_VesselSyncOutbox.sql

   Marks the manuals for download by the vessels: writes one Sync_Outbox row per vessel and
   manual, so each vessel's next pull sends them (in batches if there are many).

   A vessel gets a manual when (the same rule the pull uses):
     - it is assigned to that vessel, or to all vessels (VesselID -1), in DM_Manual_ToAck_Vsl
     - it is not deleted and has a released version (DM_ManualVersion.IsActive = 1)
   and every manual above it in the tree goes with it, so the tree is complete on board.
   A manual that is not assigned to a vessel is not sent; the last result lists those.

   Settings:
     @VesselID  -1 = every active vessel, or one VesselID
     @Apply      0 = preview the counts only, 1 = write the rows

   Safe to run again: a manual already waiting for a vessel is not added twice.
   ===================================================================================== */

SET NOCOUNT ON;

DECLARE @VesselID int = -1;
DECLARE @Apply    bit = 1;

IF OBJECT_ID('dbo.Sync_Outbox', 'U') IS NULL OR OBJECT_ID('dbo.Sync_Enqueue', 'P') IS NULL
   OR OBJECT_ID('dbo.DM_Manual', 'U') IS NULL
BEGIN
    RAISERROR('This is not the office database, or 006_VesselSyncOutbox.sql has not been run on it.', 16, 1);
    RETURN;
END

-- Vessels to mark for
DECLARE @vessels TABLE (VesselID int PRIMARY KEY);
INSERT @vessels (VesselID)
SELECT v.VesselID
  FROM dbo.V_Vessel v
 WHERE v.VesselID > 0 AND ISNULL(v.IsRetired, 0) = 0
   AND (@VesselID = -1 OR v.VesselID = @VesselID);

IF NOT EXISTS (SELECT 1 FROM @vessels)
BEGIN
    RAISERROR('No active vessel with VesselID %d in V_Vessel.', 16, 1, @VesselID);
    RETURN;
END

-- Released manuals assigned to each vessel, plus the manuals above them
DECLARE @items dbo.Sync_ItemList;

;WITH released AS
(
    SELECT DISTINCT v.VesselID, m.DM_ManualID
      FROM @vessels v
      JOIN dbo.DM_Manual_ToAck_Vsl a ON (a.VesselID = v.VesselID OR a.VesselID = -1) AND ISNULL(a.IsActive, 1) = 1
      JOIN dbo.DM_Manual m ON m.DM_ManualID = a.DM_ManualID AND ISNULL(m.IsDeleted, 0) <> 1
     WHERE EXISTS (SELECT 1 FROM dbo.DM_ManualVersion mv WHERE mv.DM_ManualID = m.DM_ManualID AND mv.IsActive = 1)
),
tree AS
(
    SELECT r.VesselID, r.DM_ManualID AS NodeID, 0 AS Lvl
      FROM released r
    UNION ALL
    SELECT t.VesselID, p.DM_ManualID, t.Lvl + 1
      FROM tree t
      JOIN dbo.DM_Manual c ON c.DM_ManualID = t.NodeID
      JOIN dbo.DM_Manual p ON p.DM_ManualID = c.ParentId AND ISNULL(p.IsDeleted, 0) <> 1
     WHERE c.ParentId <> 0 AND c.ParentId <> c.DM_ManualID AND t.Lvl < 20
)
INSERT @items (VesselID, ItemType, ItemID, Action)
SELECT DISTINCT VesselID, N'Manual', NodeID, 'U'
  FROM tree;

-- 1. Preview: per vessel, how many manuals and how many are already waiting
SELECT i.VesselID,
       COUNT(*)                                          AS ManualsToSend,
       SUM(CASE WHEN o.ItemID IS NULL THEN 1 ELSE 0 END) AS NewRows,
       SUM(CASE WHEN o.ItemID IS NULL THEN 0 ELSE 1 END) AS AlreadyWaiting
  FROM @items i
  LEFT JOIN (SELECT DISTINCT VesselID, ItemID
               FROM dbo.Sync_Outbox
              WHERE ItemType = N'Manual' AND SentUtc IS NULL AND DownloadedUtc IS NULL) o
         ON o.VesselID = i.VesselID AND o.ItemID = i.ItemID
 GROUP BY i.VesselID
 ORDER BY i.VesselID;

-- 2. Write the rows
IF @Apply = 1
BEGIN
    DECLARE @source nvarchar(128) = N'Script: 007_Sync_MarkAllManuals';
    DECLARE @start  datetime2(3)  = DATEADD(second, -1, SYSUTCDATETIME());

    EXEC dbo.Sync_Enqueue @items, @source;

    SELECT VesselID, COUNT(*) AS RowsWritten
      FROM dbo.Sync_Outbox
     WHERE SourceTable = @source AND CreatedUtc >= @start
     GROUP BY VesselID
     ORDER BY VesselID;
END
ELSE
    PRINT 'Preview only: nothing written. Set @Apply = 1 and run again to mark the manuals.';

-- 3. Released manuals not assigned to any vessel. These are not sent, except as the parent
--    of an assigned manual. Assign them in the office app (or DM_Manual_ToAck_Vsl) first.
SELECT m.DM_ManualID, m.ManualNo, m.ManualName
  FROM dbo.DM_Manual m
 WHERE ISNULL(m.IsDeleted, 0) <> 1
   AND EXISTS (SELECT 1 FROM dbo.DM_ManualVersion mv WHERE mv.DM_ManualID = m.DM_ManualID AND mv.IsActive = 1)
   AND NOT EXISTS (SELECT 1 FROM dbo.DM_Manual_ToAck_Vsl a WHERE a.DM_ManualID = m.DM_ManualID AND ISNULL(a.IsActive, 1) = 1)
 ORDER BY m.ManualNo;
