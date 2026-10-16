using System;
using System.IO;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// The server of <see cref="HlsIsolationApplicationFactory"/>, whose ffmpeg can be held before it
/// starts. The server runs a script in front of the real ffmpeg; while <see cref="HoldFile"/>
/// exists, that script waits before it hands over, so the process the server started is running
/// and has not yet opened, let alone truncated, its output.
/// </summary>
/// <remarks>
/// tesserafin#293. A residual output is overwritten by ffmpeg within milliseconds of a start, so a
/// test that does not hold ffmpeg cannot tell a server that removes the residual from one that
/// serves it until ffmpeg gets there. Held, the window is as long as the test wants it.
/// </remarks>
public sealed class HeldFfmpegApplicationFactory : HlsIsolationApplicationFactory
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tesserafin-held-ffmpeg-").FullName;

    /// <summary>
    /// Gets the file whose existence holds every ffmpeg the server starts. The server is not
    /// held when it is absent, which is the case unless a test creates it.
    /// </summary>
    public string HoldFile => Path.Combine(_directory, "hold");

    /// <inheritdoc/>
    protected override string ServerFfmpegPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                // No script runs there in front of it; a held test skips.
                return FfmpegPath;
            }

            // ffprobe is found as ffmpeg's sibling, so it goes in front as well, unheld.
            var ffmpeg = Path.Combine(_directory, "ffmpeg");
            File.WriteAllText(ffmpeg, FormattableString.Invariant($"#!/bin/sh\nwaited=0\nwhile [ -e '{HoldFile}' ] && [ $waited -lt 600 ]; do sleep 0.1; waited=$((waited + 1)); done\nexec '{FfmpegPath}' \"$@\"\n"));
            File.WriteAllText(Path.Combine(_directory, "ffprobe"), FormattableString.Invariant($"#!/bin/sh\nexec '{EndToEndMediaFixtures.FfprobePath}' \"$@\"\n"));
            foreach (var script in new[] { "ffmpeg", "ffprobe" })
            {
                File.SetUnixFileMode(Path.Combine(_directory, script), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return ffmpeg;
        }
    }
}
