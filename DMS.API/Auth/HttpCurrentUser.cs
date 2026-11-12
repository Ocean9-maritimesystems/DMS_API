using DMS.Application.Interface;

namespace DMS.API.Auth;

/// <summary><see cref="ICurrentUser"/> read from the bearer token of the current HTTP request.</summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;

    public HttpCurrentUser(IHttpContextAccessor http) => _http = http;

    public string UserId => _http.HttpContext?.User.FindFirst(DmsClaims.UserId)?.Value ?? "";

    public string UserName => _http.HttpContext?.User.FindFirst(DmsClaims.Name)?.Value is { Length: > 0 } name ? name : UserId;
}
