using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tesserafin.Server.Tests.Net;

/// <summary>
/// Structural gates: there is exactly one place that opens a listener, and the startup wizard
/// cannot reach the switch that constrains it.
/// </summary>
/// <remarks>
/// A behavioural test can only judge the code path it calls. These read the sources instead,
/// because the property being defended is an ABSENCE — no second call site, no wizard-writable
/// copy of the setting — and an absence cannot be observed by invoking anything.
/// </remarks>
public sealed class SecureBootstrapNoBypassTests
{
    /// <summary>
    /// Matches an invocation of any Kestrel <c>Listen*</c> overload — <c>Listen</c>,
    /// <c>ListenUnixSocket</c>, <c>ListenAnyIP</c>, <c>ListenLocalhost</c> and the rest.
    /// </summary>
    /// <remarks>
    /// Requires the call parentheses, so the <c>ListenWithHttps</c> property is not mistaken for a
    /// listener being opened.
    /// </remarks>
    private static readonly Regex _listenCall = new(@"\.Listen\w*\s*\(", RegexOptions.CultureInvariant);

    /// <summary>
    /// Walks up from the test binary to the checkout root.
    /// </summary>
    /// <remarks>
    /// Throws rather than skipping when the root cannot be found: a structural gate that silently
    /// inspects nothing is worse than no gate, because it reports green.
    /// </remarks>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate Tesserafin.sln above '{AppContext.BaseDirectory}'. This gate inspects sources and cannot run without them.");
    }

    private static string ReadSource(params string[] relativePath)
    {
        var full = Path.Combine(new[] { RepositoryRoot() }.Concat(relativePath).ToArray());
        Assert.True(File.Exists(full), $"Expected source file '{full}' to exist.");
        return File.ReadAllText(full);
    }

    /// <summary>
    /// Drops whole-line comments so that prose describing a listener is not mistaken for one.
    /// </summary>
    private static string WithoutCommentLines(string source)
        => string.Join(
            '\n',
            source
                .Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();
                    return !trimmed.StartsWith("//", StringComparison.Ordinal)
                        && !trimmed.StartsWith('*');
                }));

    private static bool OpensAListener(string source)
        => _listenCall.IsMatch(WithoutCommentLines(source));

    [Fact]
    public void TheEnforcementIsAppliedBeforeTheFirstListenCall()
    {
        // Comments are dropped first: prose about a listener is not a listener.
        var source = WithoutCommentLines(ReadSource("Tesserafin.Server", "Extensions", "WebHostBuilderExtensions.cs"));

        var guard = source.IndexOf("SecureBootstrap.IsEnabled", StringComparison.Ordinal);
        var constrain = source.IndexOf("SecureBootstrap.ConstrainToLoopback", StringComparison.Ordinal);
        var firstListen = _listenCall.Match(source) is { Success: true } m ? m.Index : -1;

        Assert.True(guard >= 0, "WebHostBuilderExtensions no longer consults the secure-bootstrap predicate.");
        Assert.True(constrain >= 0, "WebHostBuilderExtensions no longer constrains the bind set to loopback.");
        Assert.True(firstListen >= 0, "Expected WebHostBuilderExtensions to open a Kestrel listener.");
        Assert.True(guard < firstListen, "The secure-bootstrap check must run before the first listener is opened.");
        Assert.True(constrain < firstListen, "The loopback constraint must be applied before the first listener is opened.");
    }

    [Fact]
    public void OnlyOneFileInTheServerOpensAListener()
    {
        // Both listeners route through SetupTesserafinWebServer. A second call site would be a
        // listener the constraint never sees.
        var root = RepositoryRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "Tesserafin.Server"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFileName(path), "WebHostBuilderExtensions.cs", StringComparison.Ordinal))
            .Where(path => OpensAListener(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These files open a Kestrel listener outside the single enforcement point: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheSetupServerGoesThroughTheSharedEnforcementPoint()
    {
        var source = ReadSource("Tesserafin.Server", "ServerSetupApp", "SetupServer.cs");

        Assert.Contains("SetupTesserafinWebServer", source, StringComparison.Ordinal);
        Assert.False(OpensAListener(source), "The setup server must not open a listener of its own.");
    }

    [Fact]
    public void TheMainServerGoesThroughTheSharedEnforcementPoint()
    {
        var source = ReadSource("Tesserafin.Server", "Extensions", "WebHostBuilderExtensions.cs");

        Assert.Contains("SetupTesserafinWebServer(", source, StringComparison.Ordinal);
        Assert.Contains("UseKestrel(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWizardCannotReachTheSecureBootstrapSwitch()
    {
        // The switch lives in the startup configuration, which the wizard does not write. If any
        // API surface learned to read or write it, an unauthenticated caller inside the
        // first-time-setup window could widen the server's own bind.
        var root = RepositoryRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "Tesserafin.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("secureBootstrap", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("SecureBootstrap", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Tesserafin.Api must not reference the secure-bootstrap switch: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheSwitchIsNotStoredInTheWizardWritableNetworkConfiguration()
    {
        // network.xml is written by the wizard and by the configuration endpoints. Putting the
        // switch there would make it wizard-mutable, which is exactly what it must not be.
        var source = ReadSource("Tesserafin.Common", "Net", "NetworkConfiguration.cs");

        Assert.DoesNotContain("secureBootstrap", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFirstTimeSetupPolicyIsUnchangedInShape()
    {
        // R0-B must not weaken the first-time-setup rule. Both halves of the pre-onboarding grant —
        // the endpoint marker and the no-token requirement — must still be there.
        var source = ReadSource("Tesserafin.Api", "Auth", "FirstTimeSetupPolicy", "FirstTimeSetupHandler.cs");

        Assert.Contains("FirstTimeSetupEndpointAttribute", source, StringComparison.Ordinal);
        Assert.Contains("!authorizationInfo.HasToken", source, StringComparison.Ordinal);
        Assert.Contains("IsStartupWizardCompleted", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartupControllerStillWritesOnlyTheRemoteAccessBoolean()
    {
        // The wizard's remote-access step must remain what it is: one application-layer boolean.
        // It must not learn to touch bind addresses.
        var source = ReadSource("Tesserafin.Api", "Controllers", "StartupController.cs");

        Assert.Contains("settings.EnableRemoteAccess = startupRemoteAccessDto.EnableRemoteAccess;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalNetworkAddresses", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InternalHttpPort", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureBootstrap", source, StringComparison.Ordinal);
    }
}
