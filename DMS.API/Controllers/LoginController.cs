using DMS.API.Auth;
using DMS.Application.DTO;
using DMS.Application.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace DMS.API.Controllers;

public sealed record LoginResponse(string Token, DateTime ExpiresUtc, string UserId, string UserName, string Role);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Login against HQ_Users (hashed passwords). Replaces the hard-coded admin/admin123 user.</summary>
[Route("api/[controller]")]
[ApiController]
public class LoginController : ControllerBase
{
    private readonly IHQUserService _users;
    private readonly JwtOptions _jwt;
    private readonly ILoginService _loginService;
    private readonly ILogger<LoginController> _logger;
    private static readonly PasswordHasher<object> Hasher = new();

    public LoginController(IHQUserService users, IOptions<JwtOptions> jwt, ILoginService loginService, ILogger<LoginController> logger)
    {
        _users = users;
        _jwt = jwt.Value;
        _loginService = loginService;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] UserLogin userLogin)
    {
        if (string.IsNullOrWhiteSpace(userLogin.UserId) ||
            string.IsNullOrEmpty(userLogin.Password))
        {
            return BadRequest("User ID and password are required.");
        }

        var user = await _users.GetUserForLoginAsync(userLogin.UserId.Trim());

        if (user is null || !VerifyPassword(user.PasswordHash, userLogin.Password, out var needsRehash))
        {
            _logger.LogWarning("Failed login for {UserId}", userLogin.UserId);
            return Unauthorized("Invalid user ID or password.");
        }

        // A password still stored as plain text (or with an old hash format) is replaced by a hash now.
        if (needsRehash)
        {
            await _users.SetPasswordHashAsync(user.UserId, HashPassword(userLogin.Password));
            _logger.LogInformation("Password for {UserId} upgraded to a hash", user.UserId);
        }

        var expires = DateTime.UtcNow.AddMinutes(_jwt.AccessTokenExpireMinutes);

        var claims = new List<Claim>
        {
            new(DmsClaims.UserId, user.UserId),
            new(DmsClaims.Name, user.UserName),
            new(DmsClaims.Role, user.Role)
        };

        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new Claim(DmsClaims.Email, user.Email));
        }

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_jwt.Key));

        var token = new JwtSecurityToken(
            _jwt.Issuer,
            _jwt.Audience,
            claims,
            expires: expires,
            signingCredentials: new SigningCredentials(
                key,
                SecurityAlgorithms.HmacSha256));

        return Ok(new LoginResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires,
            user.UserId,
            user.UserName,
            user.Role));
    }

    /// <summary>Lets a logged-in user change their own password.</summary>
    [Authorize]
    [HttpPost("ChangePassword")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = User.FindFirst(DmsClaims.UserId)?.Value;
        if (userId is null) return Unauthorized();
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 10)
            return BadRequest("The new password must be at least 10 characters.");

        var user = await _users.GetUserForLoginAsync(userId);
        if (user is null || !VerifyPassword(user.PasswordHash, request.CurrentPassword ?? "", out _))
            return BadRequest("The current password is not correct.");

        await _users.SetPasswordHashAsync(userId, HashPassword(request.NewPassword));
        return NoContent();
    }

    /// <summary>Environment label for the header. Admins only: it reveals server details.</summary>
    [Authorize(Roles = "Admin")]
    [HttpGet("dbinfo")]
    public IActionResult GetDbInfo()
    {
        var (server, database) = _loginService.GetDbInfo();
        return Ok(new { server, database });
    }

    /// <summary>Used by login, change password and "dotnet run -- set-password" (see Program.cs).</summary>
    internal static string HashPassword(string password) => Hasher.HashPassword(null!, password);

    /// <summary>
    /// Checks a password against HQ_Users.PasswordHash. Older rows still hold the password as plain text:
    /// those are accepted once and flagged, so the caller replaces them with a hash. A stored value that is a
    /// real hash is never compared as text (typing the hash itself does not log in).
    /// </summary>
    internal static bool VerifyPassword(string? stored, string password, out bool needsRehash)
    {
        needsRehash = false;
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(password)) return false;

        if (LooksLikeHash(stored))
        {
            PasswordVerificationResult result;
            try
            {
                result = Hasher.VerifyHashedPassword(null!, stored, password);
            }
            catch (FormatException)
            {
                result = PasswordVerificationResult.Failed;
            }

            needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
            return result != PasswordVerificationResult.Failed;
        }

        // Legacy plain-text password (constant-time compare).
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(password));
        needsRehash = ok;
        return ok;
    }

    /// <summary>ASP.NET Core PasswordHasher output: base64, first byte 0x00 (v2, 49 bytes) or 0x01 (v3, 61+ bytes).</summary>
    private static bool LooksLikeHash(string stored)
    {
        Span<byte> buffer = stackalloc byte[256];
        if (stored.Length > 340 || !Convert.TryFromBase64String(stored, buffer, out var length)) return false;
        return (buffer[0] == 0x01 && length >= 61) || (buffer[0] == 0x00 && length == 49);
    }
}
