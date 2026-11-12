namespace DMS.Application.Interface;

/// <summary>
/// The logged-in office user of the current request, taken from the login token. Empty outside a user request
/// (for example vessel sync calls), so callers must not treat an empty value as a user.
/// </summary>
public interface ICurrentUser
{
    /// <summary>HQ_Users.UserID from the token, or "" when there is no logged-in user.</summary>
    string UserId { get; }

    /// <summary>Display name from the token, or the user ID when the token has none.</summary>
    string UserName { get; }
}
