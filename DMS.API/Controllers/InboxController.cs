using DMS.Application.Interface;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

/// <summary>The new-items inbox of the logged-in office user (dbo.DMS_Inbox / dbo.DMS_InboxRead).</summary>
[Route("api/Inbox")]
public class InboxController : DmsControllerBase
{
    private readonly IInboxService _service;

    public InboxController(IInboxService service)
    {
        _service = service;
    }

    /// <summary>GET api/Inbox?unreadOnly=&amp;itemType=&amp;search=&amp;take= - newest first.</summary>
    [HttpGet]
    public async Task<IActionResult> List(bool unreadOnly = false, string? itemType = null, string? search = null, int take = 300)
    {
        return Ok(await _service.GetListAsync(Me, unreadOnly, itemType, search, take));
    }

    /// <summary>GET api/Inbox/UnreadCount - { count } for the header badge.</summary>
    [HttpGet("UnreadCount")]
    public async Task<IActionResult> UnreadCount()
    {
        return Ok(new { count = await _service.GetUnreadCountAsync(Me) });
    }

    /// <summary>POST api/Inbox/{id}/Read?read=true|false - mark one line read or unread.</summary>
    [HttpPost("{id:long}/Read")]
    public async Task<IActionResult> MarkRead(long id, bool read = true)
    {
        return await _service.MarkReadAsync(id, read, Me) ? Ok() : NotFound();
    }

    /// <summary>POST api/Inbox/ReadAll - mark everything read.</summary>
    [HttpPost("ReadAll")]
    public async Task<IActionResult> ReadAll()
    {
        return Ok(new { count = await _service.MarkAllReadAsync(Me) });
    }
}
