using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Features;
using Microsoft.EntityFrameworkCore;

namespace DMS.Infrastructure.Services;

/// <summary>
/// Feature 2: the new-items inbox at the office. dbo.DMS_Inbox is filled by the database triggers (Database/012, 017, 018)
/// when a manual is published, a circular / alert is released or cancelled, a document is added
/// or a suggestion changes. dbo.DMS_InboxRead holds who has read which line.
///
/// An office user sees every active line except those meant for one other person (TargetUserID) or for a rank on
/// board (TargetRank): e.g. the status of a suggestion going back to the crew member who raised it.
/// </summary>
public sealed class InboxService : IInboxService
{
    private readonly GeneralContext _ctx;
    private readonly SqlDb _db;

    public InboxService(GeneralContext ctx, SqlDb db)
    {
        _ctx = ctx;
        _db = db;
    }

    /// <summary>The lines this office user may see.</summary>
    private IQueryable<DmsInbox> Visible(string me)
        => _ctx.DmsInbox.AsNoTracking()
            .Where(i => i.IsActive == 1
                        && (i.TargetUserID == null || i.TargetUserID == "" || i.TargetUserID == me)
                        && (i.TargetRank == null || i.TargetRank == ""));

    private IQueryable<DmsInbox> Unread(string me)
        => Visible(me).Where(i => !_ctx.DmsInboxReads.Any(r => r.DMS_InboxID == i.DMS_InboxID && r.UserID == me));

    public async Task<List<InboxItem>> GetListAsync(OfficeUser user, bool unreadOnly, string? itemType, string? search, int take)
    {
        take = Math.Clamp(take, 1, 1000);
        var me = user.UserId;
        var type = string.IsNullOrWhiteSpace(itemType) ? null : itemType.Trim();
        var text = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

        var query = unreadOnly ? Unread(me) : Visible(me);
        if (type != null) query = query.Where(i => i.ItemType == type);
        if (text != null) query = query.Where(i => i.Title != null && EF.Functions.Like(i.Title, text));

        return await query
            .OrderByDescending(i => i.CreatedOn).ThenByDescending(i => i.DMS_InboxID)
            .Take(take)
            .Select(i => new InboxItem
            {
                DMS_InboxID = i.DMS_InboxID,
                VesselID = i.VesselID,
                ItemType = i.ItemType,
                ItemID = i.ItemID,
                VersionID = i.VersionID,
                Action = i.Action,
                Title = i.Title,
                Priority = i.Priority,
                CreatedOn = i.CreatedOn,
                IsRead = _ctx.DmsInboxReads.Any(r => r.DMS_InboxID == i.DMS_InboxID && r.UserID == me),
            })
            .ToListAsync();
    }

    public Task<int> GetUnreadCountAsync(OfficeUser user) => Unread(user.UserId).CountAsync();

    public async Task<bool> MarkReadAsync(long inboxId, bool read, OfficeUser user)
    {
        var me = user.UserId;
        if (!await Visible(me).AnyAsync(i => i.DMS_InboxID == inboxId))
            return false;

        if (!read)
        {
            await _ctx.DmsInboxReads.Where(r => r.DMS_InboxID == inboxId && r.UserID == me).ExecuteDeleteAsync();
            return true;
        }

        if (await _ctx.DmsInboxReads.AnyAsync(r => r.DMS_InboxID == inboxId && r.UserID == me))
            return true;

        _ctx.DmsInboxReads.Add(NewRead(await _db.NextIdAsync("DMS_InboxRead"), inboxId, me));
        await _ctx.SaveChangesAsync();
        return true;
    }

    public async Task<int> MarkAllReadAsync(OfficeUser user)
    {
        var me = user.UserId;
        var unread = await Unread(me).Select(i => i.DMS_InboxID).ToListAsync();
        if (unread.Count == 0) return 0;

        // One block of IDs for all the read marks; SaveChanges writes them in one transaction.
        var readId = await _db.NextIdAsync("DMS_InboxRead", count: unread.Count);
        foreach (var inboxId in unread)
            _ctx.DmsInboxReads.Add(NewRead(readId++, inboxId, me));
        await _ctx.SaveChangesAsync();
        return unread.Count;
    }

    private static DmsInboxRead NewRead(long id, long inboxId, string userId) => new()
    {
        DMS_InboxReadID = id,
        Origin = SqlDb.Office,
        VesselID = SqlDb.OfficeOnly,
        DMS_InboxID = inboxId,
        UserID = userId,
        ReadOn = DateTime.Now,
    };
}
