using System;
using System.Linq;
using System.Reflection;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Dlna.Tests;

/// <summary>
/// Tests that the DLNA-to-domain adapter respects the one-way boundary from RFC PR91 §7: the
/// adapter may depend on the domain, but the domain must never depend back on the adapter, and
/// every public conversion on the facade must return a domain type.
/// </summary>
public static class ArchitectureTests
{
    [Fact]
    public static void DlnaPlaybackAdapter_AllPublicMethodsReturnDomainTypes()
    {
        var decisionAssembly = typeof(PlaybackDecision).Assembly;

        var publicMethods = typeof(DlnaPlaybackAdapter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName);

        Assert.NotEmpty(publicMethods);
        Assert.All(publicMethods, method => Assert.Same(decisionAssembly, method.ReturnType.Assembly));
    }

    [Fact]
    public static void DecisionAssembly_FullNameStartsWithExpectedNamespace()
    {
        var decisionAssemblyName = typeof(ClientCapabilities).Assembly.FullName;

        Assert.NotNull(decisionAssemblyName);
        Assert.StartsWith("Reefin.Playback.Decision", decisionAssemblyName, StringComparison.Ordinal);
    }

    [Fact]
    public static void DlnaAssembly_ReferencesDecisionAssembly()
    {
        var referencedNames = typeof(DlnaPlaybackAdapter).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name);

        Assert.Contains(
            referencedNames,
            name => name is not null && name.StartsWith("Reefin.Playback.Decision", StringComparison.Ordinal));
    }

    [Fact]
    public static void DecisionAssembly_DoesNotReferenceDlnaAssembly()
    {
        var referencedNames = typeof(PlaybackDecision).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name);

        Assert.DoesNotContain(
            referencedNames,
            name => name is not null && name.StartsWith("Reefin.Playback.Dlna", StringComparison.Ordinal));
    }

    /// <summary>
    /// The mirror image of <see cref="DlnaPlaybackAdapter_AllPublicMethodsReturnDomainTypes"/>: PR112b's
    /// TEMPORARY reverse adapter goes the other way, so every public method on it must return a legacy
    /// DLNA type (or <see langword="void"/>), never a <see cref="Reefin.Playback.Decision"/> type - the
    /// two facades must stay separate, not merge into one bidirectional class.
    /// </summary>
    [Fact]
    public static void ReverseDlnaAdapter_AllPublicMethodsReturnLegacyOrVoidTypes()
    {
        var decisionAssembly = typeof(PlaybackDecision).Assembly;

        var publicMethods = typeof(ReverseDlnaAdapter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName);

        Assert.NotEmpty(publicMethods);
        Assert.All(publicMethods, method => Assert.NotSame(decisionAssembly, method.ReturnType.Assembly));
    }
}
