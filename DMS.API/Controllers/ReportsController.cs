using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.API.Models.Reports;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

/// <summary>Office reports for the Reports page; exporting to Excel / PDF / CSV is done in the browser.</summary>
[Route("api/Reports")]
public class ReportsController : DmsControllerBase
{
    private readonly IReportService _service;

    public ReportsController(IReportService service)
    {
        _service = service;
    }

    /// <summary>
    /// GET api/Reports/{key}?vesselId=&amp;itemType=&amp;status=&amp;from=&amp;to=&amp;overdueOnly=&amp;days=&amp;topLevelOnly=
    /// - the rows of one report. An unknown key is 404; a report that cannot run here is 400 with the reason.
    /// </summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> Run(string key, [FromQuery] ReportRequest request)
    {
        var filter = new ReportFilter(request.VesselId, request.ItemType, request.Status, request.From, request.To,
            request.OverdueOnly, request.Days, request.TopLevelOnly);
        return Ok(await _service.RunAsync(key, filter));
    }
}
