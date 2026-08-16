using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// Binds <see cref="MintingFixture"/> to the minting tests. Separate from the media boundary
/// collection because it substitutes the transcode manager, which the boundary matrix uses for
/// real.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MintingCollection : ICollectionFixture<MintingFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "R1 playback capability minting";
}
