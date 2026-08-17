using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// Binds <see cref="WebSocketUpgradeFixture"/> to the #153-A0-R2 upgrade matrix. Its own
/// collection: the cases move a shared clock and revoke sessions, which the media matrix must not
/// see.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WebSocketUpgradeSuite : ICollectionFixture<WebSocketUpgradeFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "R2 websocket upgrade boundary";
}
