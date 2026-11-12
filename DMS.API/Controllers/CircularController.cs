using DMS.Application.DTO.Circulars;
using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Circulars;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

/// <summary>
/// Circulars: the core CIR endpoints plus the extras (ack due days, valid-until, manual links,
/// cancellation and category settings) that used to live in CircularExtrasController.
/// </summary>
[Route("api/[controller]")]
public class CircularController : DmsControllerBase
{
    private readonly ICIRService _service;
    private readonly ILogger<CircularController> _logger;

    public CircularController(ICIRService service, ILogger<CircularController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("GetCategories")]
    public async Task<ActionResult<List<CircularCategoryDto>>> GetCategories(bool isToIncludeAll, string? circularType = null)
    {
        _logger.LogInformation("Get CIR categories (circularType={circularType})", circularType);
        var categories = await _service.GetCategoriesAsync(isToIncludeAll, circularType);
        return Ok(categories);
    }

    [HttpGet("GetCIR_CategoryChartData")]
    public async Task<ActionResult<List<CircularsChart>>> GetCIR_CategoryChartData(string circularType)
    {
        _logger.LogInformation("Get Circulars chart data (circularType={circularType})", circularType);
        var chartData = await _service.GetCIR_CategoryChartDataAsync(circularType);
        return Ok(chartData);
    }

    [HttpGet("GetRecentCirculars")]
    public async Task<ActionResult<List<CIRRecentList>>> GetRecentCirculars(string circularType, int limit = 10)
    {
        _logger.LogInformation("Get Recent Circulars (circularType={circularType}, limit={limit})", circularType, limit);
        var recent = await _service.GetRecentCircularsAsync(circularType, limit);
        return Ok(recent);
    }

    [HttpGet("GetDashboardCircularCounts")]
    public async Task<ActionResult<CircularCount_Dashboard>> GetDashboardCircularCounts()
    {
        _logger.LogInformation("Get Dashboard Circular counts");
        var counts = await _service.GetDashboardCircularCountsAsync();
        return Ok(counts);
    }

    [HttpGet("GetCircularsNotifications")]
    public async Task<IActionResult> GetCircularsNotifications(int limit = 10)
    {
        var notifications = await _service.GetCIRNotificationListAsync(limit, CurrentUserId);
        return Ok(notifications);
    }


    [HttpGet("GetTreeViewCircularList")]
    public async Task<ActionResult<List<Circulars_TreeviewDto>>> GetTreeViewManualList(string? userId, string circularType)
    {
        _logger.LogInformation("Get Treeview Circular list");
        var manualList = await _service.GetTreeViewCircularsListAsync(CurrentUserId, circularType);

        return Ok(manualList);
    }

    [HttpGet("GetCIRMasterList")]
    public async Task<ActionResult<List<CIRMasterList>>> GetCIRMasterList(string? userId, long categoryId, string circularType)
    {
        _logger.LogInformation("Get CIR master list (categoryId={categoryId}, circularType={circularType})", categoryId, circularType);
        var masters = await _service.GetCIRMasterListAsync(CurrentUserId, categoryId, circularType);
        return Ok(masters);
    }

    [HttpGet("GetVesselAckList/{cir_MasterID}")]
    public async Task<ActionResult<List<CircularUserAckDto>>> GetVesselAckList(long cir_MasterID)
    {
        _logger.LogInformation("Get Vessel ack list (cir_MasterID={cir_MasterID})", cir_MasterID);
        var ackList = await _service.GetVesselAckListAsync(cir_MasterID);
        return Ok(ackList);
    }

    [HttpGet("GetNextCIRNumbersByCategory")]
    public async Task<ActionResult<string>> GetNextCIRNumbersByCategory(long categoryId, long cir_MasterID)
    {
        _logger.LogInformation("Get CIR numbers by category (categoryId={categoryId})", categoryId);
        var numbers = await _service.GetNextCIRNumbersByCategoryAsync(categoryId, cir_MasterID);
        return Ok(numbers);
    }

    [HttpGet("GetCIRDetails/{ciR_MasterID}")]
    public async Task<IActionResult> GetCIRDetails(long ciR_MasterID)
    {
        _logger.LogInformation("Get Circular details (cirMasterId={ciR_MasterID})", ciR_MasterID);
        var result = await _service.GetCIRDetailsAsync(ciR_MasterID);
        return Ok(result);
    }

    [HttpGet("GetCircularLogs")]
    public async Task<IActionResult> GetCircularLogs(long ciR_MasterID)
    {
        _logger.LogInformation("Get Circular logs (cirMasterId={ciR_MasterID})", ciR_MasterID);
        var logs = await _service.GetCircularLogsAsync(ciR_MasterID);
        return Ok(logs);
    }

    [HttpPost("AddCirMaster")]
    public async Task<ActionResult<long>> AddCIR_MasterRecord(string circularType)
    {
        var result = await _service.InsertCIR_MasterRecord(circularType);

        return result;
    }


    [HttpDelete("RemoveTempCIR")]
    public async Task<ActionResult<long>> RemoveTempCIR(long cir_MasterID)
    {
       await _service.RemoveTempCIRAsync(cir_MasterID);

        return Ok();
    }

    [HttpPost("RemoveFromFavourite")]
    public async Task<IActionResult> RemoveFromFavourite(long cir_MasterID)
    {
        await _service.RemoveFromFavouriteAsync(cir_MasterID, CurrentUserId);
        return Ok();
    }

    /// <summary>
    /// Largest Add/Edit Circular request. The attachment travels inside the JSON as base64 (a third larger than the
    /// file), so the default 30 MB request limit stopped files above about 22 MB.
    /// </summary>
    private const long MaxCircularRequestBytes = 150L * 1024 * 1024;

    [HttpPost("AddCircular")]
    [RequestSizeLimit(MaxCircularRequestBytes)]
    public async Task<ActionResult<CIR_Master>> CreateCircular([FromBody] CreateCircularDto createdDto)
    {
        if (createdDto is null)
            return BadRequest("Circular payload is required.");

        createdDto.CreatedBy = CurrentUserId; // from the token, never from the request

        var created = await _service.CreateCircularAsync(createdDto);

        return Created(string.Empty, created);
    }

    /// <summary>
    /// Save and release: the same body as EditCircular. Saves the draft's changes, then releases it to the vessels
    /// (Status Draft -> Active, ReleasedDate = now). 400 with the reason when it cannot be released (nothing is saved then).
    /// </summary>
    [HttpPut("ReleaseCircular")]
    [RequestSizeLimit(MaxCircularRequestBytes)]
    public async Task<ActionResult<long>> ReleaseCircular([FromBody] EditCircularDto editDto)
    {
        if (editDto is null)
            return BadRequest("Circular payload is required.");

        _logger.LogInformation("Save and release Circular (CIR_MasterID={CIR_MasterID})", editDto.CIR_MasterID);

        editDto.CreatedBy = CurrentUserId; // recorded as ModifiedBy
        var error = await _service.SaveAndReleaseCircularAsync(editDto, Me);
        return error == null ? Ok(editDto.CIR_MasterID) : BadRequest(error);
    }

    [HttpPut("EditCircular")]
    [RequestSizeLimit(MaxCircularRequestBytes)]
    public async Task<ActionResult<long>> EditCircular([FromBody] EditCircularDto editDto)
    {
        if (editDto is null)
            return BadRequest("Circular payload is required.");

        _logger.LogInformation("Edit Circular (CIR_MasterID={CIR_MasterID})", editDto.CIR_MasterID);

        // Only a draft can be edited; a released or cancelled circular is fixed (cancel / supersede it instead).
        var current = await _service.GetCircularesExtraInfoAsync(editDto.CIR_MasterID);
        if (current == null) return NotFound("Circular not found.");
        if (current.Status is not ("Draft" or "Temp"))
            return BadRequest($"This circular is {current.Status} and can no longer be edited.");

        // Recorded as ModifiedBy: the logged-in user, not a name sent in the request.
        editDto.CreatedBy = CurrentUserId;

        var updatedId = await _service.UpdateCircularAsync(editDto);

        return Ok(updatedId);
    }

    [HttpPost("AddToFavourite")]
    public async Task<ActionResult> AddToFavourite(long cir_MasterID, string circularType, string? userID)
    {
        await _service.AddToFavouriteAsync(cir_MasterID, circularType, CurrentUserId);
        return Ok();
    }

    [HttpPost("MarkNotificationRead")]
    public async Task<IActionResult> MarkNotificationRead([FromQuery] long? notificationID,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] MarkNotificationReadRequest? body)
    {
        var id = notificationID ?? body?.NotificationId;
        if (id is null) return BadRequest("notificationId is required.");
        return await _service.MarkNotificationReadAsync(id.Value, CurrentUserId) ? NoContent() : NotFound();
    }

    // ---------------------------------------------------------------- Extras and manual links

    [HttpGet("{id:long}/Extras")]
    public async Task<IActionResult> GetExtras(long id)
    {
        var result = await _service.GetCircularesExtraInfoAsync(id);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPut("{id:long}/Extras")]
    public async Task<IActionResult> SaveExtras(long id, [FromBody] SaveCircularExtras request)
    {
        // The service returns why it was refused (or null): a refusal is a 400 so the page shows it.
        var error = await _service.SaveCircularesExtraInfoAsync(id, request);
        return error == null ? Ok() : BadRequest(error);
    }

    [HttpPost("{id:long}/Links")]
    public async Task<IActionResult> AddLink(long id, [FromBody] AddManualLink request)
    {
        var error = await _service.AddManualLinkAsync(id, request, Me);
        return error == null ? Ok() : BadRequest(error);
    }

    [HttpDelete("Links/{linkId:long}")]
    public async Task<IActionResult> RemoveLink(long linkId)
    {
        await _service.RemoveManualLinkAsync(linkId);
        return Ok();
    }

    [HttpPost("{id:long}/Cancel")]
    public async Task<IActionResult> Cancel(long id, [FromBody] CancelCircular request)
    {
        var error = await _service.CancelCircularAsync(id, request, Me);
        return error == null ? Ok() : BadRequest(error);
    }

    /// <summary>Releases a draft circular / alert to the vessels (Draft -> Active).</summary>
    [HttpPost("{id:long}/Release")]
    public async Task<IActionResult> Release(long id)
    {
        var error = await _service.ReleaseCircularAsync(id, Me);
        return error == null ? Ok() : BadRequest(error);
    }

    [HttpGet("Manual/{manualId:long}/Circulars")]
    public async Task<IActionResult> ForManual(long manualId)
    {
        return Ok(await _service.GetForManualAsync(manualId));
    }

    /// <summary>After a manual revision takes in the amendments: close its Amends / Supersedes links.</summary>
    [HttpPost("Manual/{manualId:long}/CloseLinks")]
    public async Task<IActionResult> CloseLinks(long manualId)
    {
        var result = new { closed = await _service.CloseLinksForManualAsync(manualId) };
        return Ok(result);
    }

    [HttpGet("Manual/{manualId:long}/Extras")]
    public async Task<IActionResult> GetManualExtras(long manualId)
    {
        var extras = await _service.GetManualAsync(manualId);
        return extras == null ? NotFound() : Ok(extras);
    }

    [HttpPut("Manual/{manualId:long}/Extras")]
    public async Task<IActionResult> SaveManualExtras(long manualId, [FromBody] ManualExtras request)
    {
        await _service.SaveManualAsync(manualId, request.AckDueDays);
        return Ok();
    }

    [HttpGet("CategorySettings")]
    public async Task<IActionResult> CategorySettings()
    {
        var result = await _service.GetCategoriesAsync();
        return Ok(result);
    }

    [HttpPut("CategorySettings/{categoryId:long}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SaveCategorySetting(long categoryId, [FromBody] CircularCategorySetting request)
    {
        await _service.SaveCategoryAsync(categoryId, request.RequiresValidUntil);
        return Ok();
    }
}
