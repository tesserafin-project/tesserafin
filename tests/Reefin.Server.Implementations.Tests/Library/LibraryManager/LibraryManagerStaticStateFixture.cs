using Xunit;

namespace Reefin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// <see cref="Reefin.Server.Core.Library.LibraryManager"/> mutates process-wide statics on
/// <c>BaseItem</c> (<c>ConfigurationManager</c>, <c>LibraryManager</c>) when a test needs to
/// exercise <c>BaseItem.GetInternalMetadataPath()</c> or tag-based visibility. Tests that touch
/// those statics must run sequentially with each other, hence this dedicated, non-parallel
/// collection (mirrors <c>BaseItemStaticStateFixture</c> in Reefin.Controller.Tests, which this
/// project does not have access to).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class LibraryManagerStaticStateFixture
{
    public const string Name = "LibraryManager item lookup static state";
}
