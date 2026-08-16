using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// Binds <see cref="MediaBoundaryFixture"/> to every class in the matrix, so one server boot and
/// one seeded library serve the whole suite.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MediaBoundaryCollection : ICollectionFixture<MediaBoundaryFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "R1 media authorization boundary";
}
