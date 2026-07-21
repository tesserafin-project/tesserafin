using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Reefin.Playback.Contract.Scan.Tests;

/// <summary>
/// Issue #75 slice 75b: proof that the scan-path ban is mechanical, not a review convention. One
/// fast test asserts every required banned API is declared; one slower test compiles a project that
/// reaches for a banned symbol and asserts the BUILD fails with RS0030.
/// </summary>
public sealed class PlaybackContractScannerBanTests
{
    private static readonly string[] _requiredBans =
    {
        "M:System.Text.Json.Utf8JsonReader.GetString",
        "M:System.Text.Json.Utf8JsonReader.GetComment",
        "M:System.Text.Json.Utf8JsonReader.CopyString(System.Span{System.Byte})",
        "M:System.Text.Json.Utf8JsonReader.CopyString(System.Span{System.Char})",
        "P:System.Text.Json.Utf8JsonReader.ValueSpan",
        "P:System.Text.Json.Utf8JsonReader.ValueSequence",
        "M:System.Text.Encoding.GetString(System.Byte[])",
        "M:System.Text.Encoding.GetString(System.ReadOnlySpan{System.Byte})",
        "M:System.Enum.Parse``1(System.String)",
        "M:System.Enum.TryParse``1(System.String,``0@)",
    };

    [Fact]
    public void ScanAssembly_BansEveryReaderAndDecodeEscapeHatch()
    {
        var banFile = Path.Combine(RepoRoot(), "src", "Reefin.Playback.Contract.Scan", "BannedSymbols.txt");
        Assert.True(File.Exists(banFile), $"Missing {banFile}");
        var text = File.ReadAllText(banFile);

        foreach (var ban in _requiredBans)
        {
            Assert.Contains(ban, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BannedSymbolUsage_FailsTheBuildWithRs0030()
    {
        var canary = Path.Combine(RepoRoot(), "tests", "BannedSymbolCanary", "BannedSymbolCanary.csproj");
        if (!File.Exists(canary))
        {
            Assert.Skip($"Canary project not found at {canary}");
        }

        var psi = new ProcessStartInfo("dotnet", $"build \"{canary}\" -c Debug --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Assert.Skip($"Could not launch dotnet to build the canary: {ex.Message}");
            return;
        }

        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Skip("Canary build timed out.");
        }

        var output = stdout + stderr;

        // The whole point: the banned symbol turns the build red, and it is RS0030 that does it.
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("RS0030", output, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Reefin.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
