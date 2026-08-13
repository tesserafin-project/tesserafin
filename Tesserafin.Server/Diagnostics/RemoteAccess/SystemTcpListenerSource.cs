using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Observes TCP listeners through the operating system's read-only listing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPGlobalProperties.GetActiveTcpListeners"/> returns the whole listener table
/// without needing elevation and without naming any process. That is exactly the amount of
/// information this layer wants: enough to say something is there, not enough to say what.
/// </para>
/// <para>
/// The table is read once and filtered in memory. Nothing here binds a port, connects to one, or
/// asks who owns a socket, and a failure to read is reported as such rather than being read as
/// "nothing is listening" — the difference between "I looked and saw none" and "I could not look"
/// is the whole reason <see cref="ListenerObservationOutcome"/> has five values.
/// </para>
/// </remarks>
public sealed class SystemTcpListenerSource : ITcpListenerSource
{
    /// <inheritdoc />
    public IReadOnlyList<PortListenerObservation> Observe(IReadOnlyList<int> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        IPEndPoint[] listeners;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (NetworkInformationException)
        {
            return Unavailable(ports, ListenerObservationOutcome.InspectionDenied);
        }
        catch (PlatformNotSupportedException)
        {
            return Unavailable(ports, ListenerObservationOutcome.Unsupported);
        }
        catch (NotImplementedException)
        {
            return Unavailable(ports, ListenerObservationOutcome.Unsupported);
        }

        var observedPorts = new HashSet<int>();
        foreach (var listener in listeners)
        {
            observedPorts.Add(listener.Port);
        }

        var result = new List<PortListenerObservation>(ports.Count);
        foreach (var port in ports)
        {
            result.Add(new PortListenerObservation(
                port,
                observedPorts.Contains(port)
                    ? ListenerObservationOutcome.ObservedListener
                    : ListenerObservationOutcome.NoListenerObserved));
        }

        return result;
    }

    private static IReadOnlyList<PortListenerObservation> Unavailable(IReadOnlyList<int> ports, ListenerObservationOutcome outcome)
    {
        var result = new List<PortListenerObservation>(ports.Count);
        foreach (var port in ports)
        {
            result.Add(new PortListenerObservation(port, outcome));
        }

        return result;
    }
}
