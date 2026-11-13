-- 035_Check_DuplicateManualNo.sql
-- Read only: lists manual numbers used more than once under the same parent (e.g. two "1.10").
-- They came from the old numbering, which compared numbers as text; new manuals are now numbered correctly.
-- Nothing is changed here: renumbering a released manual changes the number the vessels see, so decide per row
-- and update DM_Manual.ManualNo by hand (the change syncs to the vessels like any other edit).

SELECT m.ParentId,
       p.ManualNo  AS ParentNo,
       m.ManualNo,
       m.DM_ManualID,
       m.ManualName,
       m.IsDeleted,
       m.CreatedDate
FROM dbo.DM_Manual m
LEFT JOIN dbo.DM_Manual p ON p.DM_ManualID = m.ParentId
WHERE EXISTS (SELECT 1
              FROM dbo.DM_Manual d
              WHERE d.ParentId = m.ParentId
                AND d.ManualNo = m.ManualNo
                AND d.DM_ManualID <> m.DM_ManualID)
ORDER BY m.ParentId, m.ManualNo, m.CreatedDate;
