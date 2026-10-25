/* =====================================================================================================
   018_CancelDraftCircular.sql  -  run on the OFFICE (HQ) database. Safe to run more than once.

   A draft circular / alert can now be cancelled (api/Circular/{id}/Cancel). A draft was never sent to the
   vessels, so cancelling it must not put a "Cancelled" entry in their inbox.

   TR_CIR_Master_Inbox (012_NewFeatures.sql) is recreated with one change: "Cancelled" is posted only when the
   circular was released before (old status not Temp / Draft / Cancelled). Everything else is as in 012:
     - released (Temp/Draft -> any other status, or inserted released)  -> "New" for every vessel (-1)
     - cancelled after it was released                                  -> "Cancelled" for every vessel
   ===================================================================================================== */
SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.Inbox_Add', 'P') IS NULL
BEGIN
    PRINT '018: dbo.Inbox_Add not found - run 012_NewFeatures.sql first.';
    RETURN;
END;
GO

select * from DMS_Inbox

CREATE OR ALTER TRIGGER dbo.TR_CIR_Master_Inbox ON dbo.CIR_Master AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ID bigint, ItemType varchar(20), Action varchar(20), Title nvarchar(500), Priority int);
    INSERT @rows
    SELECT i.CIR_MasterID,
           CASE WHEN i.CircularType LIKE 'Alert%' THEN 'Alert' ELSE 'Circular' END,
           CASE WHEN i.Status = 'Cancelled' THEN 'Cancelled' ELSE 'New' END,
           LEFT(LTRIM(ISNULL(i.CIR_Number, '') + ' ' + ISNULL(i.Title, '')), 500), i.Priority
      FROM inserted i
      LEFT JOIN deleted d ON d.CIR_MasterID = i.CIR_MasterID
     WHERE ISNULL(i.IsDeleted, 0) <> 1
       AND (   (ISNULL(i.Status, '') NOT IN ('Temp', 'Draft', 'Cancelled') AND (d.CIR_MasterID IS NULL OR ISNULL(d.Status, '') IN ('Temp', 'Draft')))
            -- Cancelled: only one the vessels had (a cancelled draft was never sent).
            OR (i.Status = 'Cancelled' AND d.CIR_MasterID IS NOT NULL AND ISNULL(d.Status, '') NOT IN ('Temp', 'Draft', 'Cancelled')));
    DECLARE @id bigint, @type varchar(20), @action varchar(20), @title nvarchar(500), @priority int;
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ID, ItemType, Action, Title, Priority FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @id, @type, @action, @title, @priority;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.Inbox_Add -1, @type, @id, NULL, @action, @title, @priority;
        FETCH NEXT FROM c INTO @id, @type, @action, @title, @priority;
    END
    CLOSE c; DEALLOCATE c;
END
GO

PRINT '018_CancelDraftCircular: TR_CIR_Master_Inbox updated.';
GO
