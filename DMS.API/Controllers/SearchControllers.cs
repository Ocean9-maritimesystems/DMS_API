using DMS.Application.Interface;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

[Route("api/Search")]
public class SearchController : DmsControllerBase
{
    private readonly ISearchService _service;

    public SearchController(ISearchService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Search(string q, string? types)
    {
        return Ok(await _service.SearchAsync(q ?? "", types, Me));
    }
}
