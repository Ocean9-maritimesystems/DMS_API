using DMS.Data.EF;
using DMS.Data.EF.Models.Features;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Features;

/// <summary>
/// Which notifications each office user has read (dbo.Notification_Read, Database/015_BackendFixes.sql).
/// Notifications are shared by everyone (UserID ''), so "read" has to be kept per user: one user opening a
/// notification no longer marks it read for all. Until the script is run, the old shared IsRead flag is used.
/// </summary>
public static class NotificationReads
{
    public const string Manual = "DM";
    public const string Circular = "CIR";

    /// <summary>Largest number of notifications one call returns.</summary>
    public const int MaxLimit = 100;

    private static bool _installed;

    public static int ClampLimit(int limit) => limit <= 0 ? 10 : Math.Min(limit, MaxLimit);

    public static async Task<bool> IsInstalledAsync(DbContext db)
    {
        if (_installed) return true;
        return _installed = await db.TableExistsAsync("Notification_Read");
    }

    /// <summary>The notification IDs of one module this user has read (notifications are listed for 30 days).</summary>
    /// <remarks>
    /// Plain SQL, so it works from any context (the manuals and circulars services pass their own, which do not map
    /// Notification_Read).
    /// </remarks>
    public static async Task<HashSet<long>> ReadIdsAsync(DbContext db, string module, string userId)
    {
        var since = DateTime.UtcNow.AddDays(-35);
        var ids = await db.Database
            .SqlQuery<long>($"SELECT NotificationID AS [Value] FROM dbo.Notification_Read WHERE Module = {module} AND UserID = {userId} AND ReadOn >= {since}")
            .ToListAsync();
        return ids.ToHashSet();
    }

    /// <summary>
    /// Records that this user has read one notification (once). The row gets its key from Proc_NextID (an office
    /// row: Origin HQ, VesselID -1), like every other table.
    /// </summary>
    public static async Task MarkReadAsync(DbContext db, string module, long notificationId, string userId)
    {
        var already = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.Notification_Read WHERE Module = {module} AND NotificationID = {notificationId} AND UserID = {userId}")
            .ToListAsync();
        if (already.FirstOrDefault() > 0) return;

        var id = await db.NextIdAsync("Notification_Read");
        // A second click at the same moment finds the row there and adds nothing.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            IF NOT EXISTS (SELECT 1 FROM dbo.Notification_Read WHERE Module = {module} AND NotificationID = {notificationId} AND UserID = {userId})
                INSERT dbo.Notification_Read (Notification_ReadID, Origin, VesselID, Module, NotificationID, UserID, ReadOn)
                VALUES ({id}, 'HQ', -1, {module}, {notificationId}, {userId}, SYSUTCDATETIME());
            """);
    }
}

public static class DatabaseObjects
{
    /// <summary>True when dbo.{table} exists (stored procedure dbo.Api_TableExists).</summary>
    public static async Task<bool> TableExistsAsync(this DbContext db, string table)
    {
        var found = await db.Database
            .SqlQuery<int>($"EXEC dbo.Api_TableExists @TableName = {table}")
            .ToListAsync();
        return found.FirstOrDefault() == 1;
    }
}
