using System.Collections.Generic;
using Tesserafin.Controller.Net;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// One upgrade the server accepted, as observed by <see cref="UpgradeRecorder"/>.
/// </summary>
/// <param name="Claims">The claims the principal carried, in issue order.</param>
/// <param name="AuthenticationType">The identity's authentication type, i.e. the scheme.</param>
/// <param name="AuthorizationInfo">The authorization the connection was built with.</param>
public sealed record AcceptedUpgrade(
    IReadOnlyList<ClaimRecord> Claims,
    string? AuthenticationType,
    AuthorizationInfo? AuthorizationInfo);
