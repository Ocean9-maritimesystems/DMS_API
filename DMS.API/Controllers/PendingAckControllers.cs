using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

[Route("api/Pending")]
public class PendingAckController : DmsControllerBase
{
    private readonly IAckDashboardService _service;

    public PendingAckController(IAckDashboardService service)
    {
        _service = service;
    }

    [HttpGet("Vessels")]
    public async Task<IActionResult> Vessels()
    {
        return Ok(await _service.GetVesselSummaryAsync());
    }

    [HttpGet("Vessel")]
    public async Task<IActionResult> Vessel(int? vesselId, string? itemType, string? rank)
    {
        return Ok(await _service.GetVesselPendingAsync(vesselId, itemType, rank));
    }

    /// <summary>Office users' pending items; anyone but an administrator sees only their own.</summary>
    [HttpGet("Office")]
    public async Task<IActionResult> Office(string? userId)
    {
        return Ok(await _service.GetOfficePendingAsync(IsAdmin ? userId : CurrentUserId));
    }

    [HttpGet("Mine")]
    public async Task<IActionResult> Mine()
    {
        return Ok(await _service.GetOfficePendingAsync(CurrentUserId));
    }

    [HttpGet("Settings")]
    public async Task<IActionResult> GetSettings() => Ok(await _service.GetSettingsAsync());

    [HttpPut("Settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SaveSettings([FromBody] FeatureSettingsDto settings)
    {
        await _service.SaveSettingsAsync(settings);
        return Ok();
    }
}