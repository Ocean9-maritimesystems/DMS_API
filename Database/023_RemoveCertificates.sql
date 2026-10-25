/* =====================================================================================================
   023_RemoveCertificates.sql  -  run on the OFFICE (HQ) database after 022. Safe to run more than once.

   Certificates are no longer a separate kind of Other Document (removed from the office and the vessel apps):

     - A new document is always "Document" in the inbox (TR_OtherDocumentMaster_Inbox), whatever its type.
     - Inbox entries already made: "Certificate" / New becomes "Document" / New; the Expiring / Expired
       reminders are switched off (IsActive = 0). The changes reach the ships with the normal row sync.
     - dbo.Inbox_CertificateExpiry and dbo.Rpt_CertificateExpiry are dropped.

   Run 020_ApiProcedures.sql again as well: its search (dbo.Api_Search) no longer labels results "Certificate".

   The certificate columns are left in place with their data (OtherDocument_Type.IsCertificate / WarningDays,
   OtherDocumentMaster.CertificateNo / IssuedBy / IssueDate / ExpiryDate / NextSurveyDate / ReplacesID): nothing
   reads or writes them any more, and they can be dropped later if the data is not wanted.
   ===================================================================================================== */
SET NOCOUNT ON;
GO

/* ------------------------------------------------------------- New documents in the inbox ---- */
IF OBJECT_ID('dbo.Inbox_Add', 'P') IS NOT NULL AND COL_LENGTH('dbo.OtherDocumentMaster', 'IsForOfficeOnly') IS NOT NULL
EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_OtherDocumentMaster_Inbox ON dbo.OtherDocumentMaster AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ID bigint, VesselID int, Title nvarchar(500));
    INSERT @rows
    SELECT i.OtherDocumentMasterID,
           CASE WHEN i.IsForOfficeOnly = 1 THEN -2 ELSE i.VesselID END,
           LEFT(LTRIM(ISNULL(i.DocumentNumber, '''') + '' '' + ISNULL(i.DocumentTitle, '''')), 500)
      FROM inserted i
     WHERE ISNULL(i.IsDeleted, 0) <> 1 AND ISNULL(i.DocumentTitle, '''') <> '''';
    DECLARE @id bigint, @vessel int, @title nvarchar(500);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ID, VesselID, Title FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @id, @vessel, @title;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.Inbox_Add @vessel, ''Document'', @id, NULL, ''New'', @title, NULL;
        FETCH NEXT FROM c INTO @id, @vessel, @title;
    END
    CLOSE c; DEALLOCATE c;
END');
GO

/* ------------------------------------------------------------------- Existing inbox entries ---- */
IF OBJECT_ID('dbo.DMS_Inbox', 'U') IS NOT NULL
BEGIN
    UPDATE dbo.DMS_Inbox SET ItemType = 'Document' WHERE ItemType = 'Certificate' AND Action = 'New';
    PRINT CONCAT(@@ROWCOUNT, ' certificate inbox entr(ies) are now documents.');
    UPDATE dbo.DMS_Inbox SET IsActive = 0 WHERE ItemType = 'Certificate' AND IsActive <> 0;
    PRINT CONCAT(@@ROWCOUNT, ' certificate expiry reminder(s) switched off.');
END
GO

/* -------------------------------------------------------------------------- Unused procedures ---- */
IF OBJECT_ID('dbo.Inbox_CertificateExpiry', 'P') IS NOT NULL DROP PROCEDURE dbo.Inbox_CertificateExpiry;
IF OBJECT_ID('dbo.Rpt_CertificateExpiry', 'P') IS NOT NULL DROP PROCEDURE dbo.Rpt_CertificateExpiry;
GO

PRINT '023_RemoveCertificates: done. Run 020_ApiProcedures.sql again for the search.';
GO
