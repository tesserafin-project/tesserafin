namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// What the server's own listening configuration looks like.
/// </summary>
public sealed class RemoteAccessBackendObservationDto
{
    /// <summary>Gets or sets a value indicating whether secure bootstrap is active.</summary>
    public bool SecureBootstrapActive { get; set; }

    /// <summary>Gets or sets how the backend's listening addresses are constrained.</summary>
    public RemoteAccessBackendBindPosture Posture { get; set; }

    /// <summary>Gets or sets a value indicating whether a unix socket is configured.</summary>
    public bool UnixSocketConfigured { get; set; }

    /// <summary>Gets or sets the internal HTTP port.</summary>
    public int InternalHttpPort { get; set; }

    /// <summary>Gets or sets the internal HTTPS port.</summary>
    public int InternalHttpsPort { get; set; }
}
