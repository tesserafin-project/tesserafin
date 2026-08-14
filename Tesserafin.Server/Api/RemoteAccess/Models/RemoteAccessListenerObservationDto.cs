namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// Whether something is already listening on the ingress ports.
/// </summary>
public sealed class RemoteAccessListenerObservationDto
{
    /// <summary>Gets or sets the inspected port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets what the inspection produced.</summary>
    public RemoteAccessListenerOutcome Outcome { get; set; }
}
