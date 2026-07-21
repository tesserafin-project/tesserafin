using System.Collections.Immutable;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// A fully-resolved invocation of an ffmpeg/ffprobe-family executable.
/// </summary>
/// <param name="Executable">Path to the executable.</param>
/// <param name="Arguments">
/// The command-line argument segments. Today this always contains exactly one element - the
/// already-assembled argument string - because the pipeline still builds one long string
/// (<see cref="EncodingHelper"/> and friends). It is kept as an array so callers can migrate to
/// discrete arguments later without changing this type's shape.
/// </param>
/// <param name="EnvironmentVariables">
/// Environment variables to set on the child process only. Never mutate
/// <see cref="System.Environment"/> for values that belong here - that leaks into every other
/// process the server spawns concurrently (probes, other transcodes, unrelated child processes).
/// </param>
/// <param name="WorkingDirectory">Working directory for the process, or null/empty for the current one.</param>
public sealed record FfmpegCommand(
    string Executable,
    ImmutableArray<string> Arguments,
    ImmutableDictionary<string, string> EnvironmentVariables,
    string? WorkingDirectory = null)
{
    /// <summary>
    /// Builds a command from a single pre-assembled argument-line string, matching how the
    /// pipeline builds arguments today.
    /// </summary>
    /// <param name="executable">Path to the executable.</param>
    /// <param name="argumentLine">The full argument string, as would be passed to <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/>.</param>
    /// <param name="environmentVariables">Environment variables to set on the child process only.</param>
    /// <param name="workingDirectory">Working directory for the process, or null for the current one.</param>
    /// <returns>The constructed <see cref="FfmpegCommand"/>.</returns>
    public static FfmpegCommand FromArgumentLine(
        string executable,
        string argumentLine,
        ImmutableDictionary<string, string>? environmentVariables = null,
        string? workingDirectory = null)
        => new(executable, [argumentLine], environmentVariables ?? ImmutableDictionary<string, string>.Empty, workingDirectory);

    /// <summary>
    /// Gets the argument line to hand to <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/>.
    /// </summary>
    /// <returns>The joined argument string.</returns>
    public string ToArgumentLine() => string.Join(' ', Arguments);
}
