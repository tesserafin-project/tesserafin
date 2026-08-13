namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// What a read-only listing said about one TCP port.
/// </summary>
/// <param name="Port">The port examined. Only 80 and 443 are ever examined.</param>
/// <param name="Outcome">What the listing showed, or why it could not show anything.</param>
public sealed record PortListenerObservation(int Port, ListenerObservationOutcome Outcome);
