using System;
using System.Linq;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Decision.Tests;

/// <summary>
/// Tests that the domain assembly does not couple back to the DLNA/legacy model it was split out
/// of. The real guarantee is structural: <c>Reefin.Playback.Decision.csproj</c> has no
/// <c>ProjectReference</c> at all, so the compiler physically cannot resolve a
/// <c>Reefin.Model</c>/<c>Reefin.Controller</c> type. This test is a weaker, secondary signal -
/// <see cref="Type.Assembly"/>.<see cref="System.Reflection.Assembly.GetReferencedAssemblies"/>
/// only lists assemblies the compiler actually emitted a reference to, and the compiler elides
/// referenced assemblies that end up unused, so an unused <c>ProjectReference</c> would not show
/// up here even though it would violate the intent. Absence-of-ProjectReference in the csproj is
/// the actual enforcement mechanism; this test just catches the common case where a type from
/// those namespaces is actually used.
/// </summary>
public static class ArchitectureTests
{
    [Fact]
    public static void Assembly_DoesNotReferenceModelOrControllerAssemblies()
    {
        var referencedAssemblyNames = typeof(PlaybackDecision).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name);

        Assert.DoesNotContain(
            referencedAssemblyNames,
            name => name is not null &&
                (name.StartsWith("Reefin.Model", StringComparison.Ordinal) ||
                 name.StartsWith("Reefin.Controller", StringComparison.Ordinal)));
    }
}
