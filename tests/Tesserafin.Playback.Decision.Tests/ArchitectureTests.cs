using System;
using System.Linq;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Decision.Tests;

/// <summary>
/// Tests that the domain assembly does not couple back to the DLNA/legacy model it was split out
/// of. The real guarantee is structural: <c>Tesserafin.Playback.Decision.csproj</c> has no
/// <c>ProjectReference</c> at all, so the compiler physically cannot resolve a
/// <c>Tesserafin.Model</c>/<c>Tesserafin.Controller</c> type. This test is a weaker, secondary signal -
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
                (name.StartsWith("Tesserafin.Model", StringComparison.Ordinal) ||
                 name.StartsWith("Tesserafin.Controller", StringComparison.Ordinal)));
    }
}
