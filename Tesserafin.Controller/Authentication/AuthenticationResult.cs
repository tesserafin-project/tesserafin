#nullable disable

using Tesserafin.Model.Dto;

namespace Tesserafin.Controller.Authentication;

/// <summary>
/// A class representing an authentication result.
/// </summary>
public class AuthenticationResult
{
    /// <summary>
    /// Gets or sets the user.
    /// </summary>
    public UserDto User { get; set; }

    /// <summary>
    /// Gets or sets the session info.
    /// </summary>
    public SessionInfoDto SessionInfo { get; set; }

    /// <summary>
    /// Gets or sets the access token.
    /// </summary>
    public string AccessToken { get; set; }

    /// <summary>
    /// Gets or sets the server id.
    /// </summary>
    public string ServerId { get; set; }
}
