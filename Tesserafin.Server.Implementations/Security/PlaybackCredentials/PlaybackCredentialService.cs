using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Server.Implementations.Security.PlaybackCredentials;

/// <summary>
/// The in-process store and validator for playback capabilities and WebSocket tickets (#153-A0).
/// </summary>
/// <remarks>
/// WHY IN MEMORY. <c>SessionManager</c> holds <c>_activeConnections</c> as a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>: sessions already die with the process and are
/// already not shared between instances. A database-backed capability would outlive the in-memory
/// session it is bound to and could not be validated against it after a restart, so it would offer
/// durability this server cannot actually honour. Matching the existing lifetime is the honest
/// choice, and the limits it implies are written down in
/// <c>docs/playback-credential-server-contract.md</c> rather than papered over.
///
/// WHAT IS STORED. Never the presented value. Each entry keeps a SHA-256 verifier, and lookup
/// hashes the presentation and finds the entry by that digest. The final acceptance still runs
/// <see cref="CryptographicOperations.FixedTimeEquals"/> over the two digests, so the one
/// comparison that decides acceptance is constant-time regardless of what the dictionary did.
///
/// WHY REVOKED AND CONSUMED ENTRIES ARE KEPT BRIEFLY. So the tests can prove WHY something was
/// refused — "revoked" and "replayed" are the two properties this design exists to provide, and a
/// store that forgets them can only ever say "unknown". They are held no longer than the original
/// credential would have lived, and every caller-visible response is identical either way, so
/// nothing leaks to a prober.
/// </remarks>
public sealed class PlaybackCredentialService : IPlaybackCredentialService
{
    /// <summary>
    /// Secret size. 256 bits, against a contract minimum of 128.
    /// </summary>
    public const int SecretByteCount = 32;

    /// <summary>
    /// How long a freshly minted playback capability is accepted.
    /// </summary>
    public static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How close to expiry a capability must be before renewal is allowed.
    /// </summary>
    public static readonly TimeSpan CapabilityRenewalWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a WebSocket ticket is accepted. Seconds, because it is consumed during a handshake
    /// that begins immediately after it is minted.
    /// </summary>
    public static readonly TimeSpan WebSocketTicketLifetime = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly IRandomSecretSource _randomSecretSource;

    private readonly ConcurrentDictionary<string, CapabilityEntry> _capabilitiesByVerifier = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CapabilityEntry> _capabilitiesById = new();
    private readonly ConcurrentDictionary<string, RetiredEntry> _retiredCapabilities = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, TicketEntry> _ticketsByVerifier = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RetiredEntry> _retiredTickets = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCredentialService"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock. Read at validation, not only at minting.</param>
    /// <param name="randomSecretSource">The randomness boundary.</param>
    public PlaybackCredentialService(TimeProvider timeProvider, IRandomSecretSource randomSecretSource)
    {
        _timeProvider = timeProvider;
        _randomSecretSource = randomSecretSource;
    }

    /// <inheritdoc />
    public PlaybackCapabilityGrant MintCapability(PlaybackCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Scopes);
        if (request.Scopes.Count == 0)
        {
            throw new ArgumentException("A capability with no scope would grant nothing and mean nothing.", nameof(request));
        }

        var now = _timeProvider.GetUtcNow();
        Prune(now);

        var (value, verifier) = NewSecret();
        var entry = new CapabilityEntry(
            Guid.NewGuid(),
            verifier,
            request.UserId,
            request.SessionId,
            request.DeviceId,
            request.PlaySessionId,
            request.ItemId,
            request.MediaSourceId,
            request.Scopes.Distinct().ToArray(),
            now,
            now + CapabilityLifetime);

        _capabilitiesByVerifier[verifier] = entry;
        _capabilitiesById[entry.CapabilityId] = entry;

        return new PlaybackCapabilityGrant(
            entry.CapabilityId,
            value,
            entry.IssuedAt,
            entry.ExpiresAt,
            entry.Scopes,
            entry.ItemId,
            entry.MediaSourceId,
            entry.PlaySessionId);
    }

    /// <inheritdoc />
    public PlaybackCapabilityRenewal RenewCapability(Guid capabilityId, string sessionId)
    {
        var now = _timeProvider.GetUtcNow();

        if (!_capabilitiesById.TryGetValue(capabilityId, out var entry))
        {
            // Retired for either reason answers the same way to the caller.
            return Failed(PlaybackCapabilityFailure.Unknown);
        }

        if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
        {
            return Failed(PlaybackCapabilityFailure.SessionMismatch);
        }

        if (now >= entry.ExpiresAt)
        {
            // Not resurrectable. Mint a new one with the durable token instead.
            return Failed(PlaybackCapabilityFailure.RenewalAfterExpiry);
        }

        if (entry.ExpiresAt - now > CapabilityRenewalWindow)
        {
            // Renewing from the moment of issue would chain a short-lived credential into a durable
            // one with extra steps, which is exactly the property this design removes.
            return Failed(PlaybackCapabilityFailure.RenewalTooEarly);
        }

        entry.ExpiresAt = now + CapabilityLifetime;
        return new PlaybackCapabilityRenewal(true, PlaybackCapabilityFailure.None, entry.CapabilityId, entry.IssuedAt, entry.ExpiresAt);

        PlaybackCapabilityRenewal Failed(PlaybackCapabilityFailure failure)
            => new(false, failure, capabilityId, default, default);
    }

    /// <inheritdoc />
    public PlaybackCapabilityValidation ValidateCapability(string? presentedValue, PlaybackCapabilityDemand demand)
    {
        if (string.IsNullOrEmpty(presentedValue))
        {
            return Refused(PlaybackCapabilityFailure.Missing);
        }

        var verifier = Verifier(presentedValue);
        var now = _timeProvider.GetUtcNow();

        if (!_capabilitiesByVerifier.TryGetValue(verifier, out var entry))
        {
            return Refused(_retiredCapabilities.ContainsKey(verifier)
                ? PlaybackCapabilityFailure.Revoked
                : PlaybackCapabilityFailure.Unknown);
        }

        // The dictionary already found it; this is the comparison that ACCEPTS it, and it is
        // constant-time so that acceptance never depends on how far two digests agree.
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(verifier),
                Convert.FromHexString(entry.Verifier)))
        {
            return Refused(PlaybackCapabilityFailure.Unknown);
        }

        if (now >= entry.ExpiresAt)
        {
            return Refused(PlaybackCapabilityFailure.Expired);
        }

        if (!entry.Scopes.Contains(demand.Scope))
        {
            return Refused(PlaybackCapabilityFailure.ScopeMismatch);
        }

        // Item and media source are checked only for a scope that carries them. Fonts deliberately
        // has neither, and demanding one here would make a font capability impossible to satisfy.
        if (entry.ItemId is not null && demand.ItemId is not null && !entry.ItemId.Value.Equals(demand.ItemId.Value))
        {
            return Refused(PlaybackCapabilityFailure.ItemMismatch);
        }

        if (entry.ItemId is null && demand.ItemId is not null && demand.Scope != PlaybackCapabilityScope.Fonts)
        {
            return Refused(PlaybackCapabilityFailure.ItemMismatch);
        }

        if (entry.MediaSourceId is not null
            && demand.MediaSourceId is not null
            && !string.Equals(entry.MediaSourceId, demand.MediaSourceId, StringComparison.Ordinal))
        {
            return Refused(PlaybackCapabilityFailure.MediaSourceMismatch);
        }

        return new PlaybackCapabilityValidation(true, PlaybackCapabilityFailure.None, entry.UserId, entry.SessionId, entry.PlaySessionId);

        static PlaybackCapabilityValidation Refused(PlaybackCapabilityFailure failure)
            => new(false, failure, Guid.Empty, null, null);
    }

    /// <inheritdoc />
    public WebSocketTicketGrant MintWebSocketTicket(WebSocketTicketRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        Prune(now);

        var (value, verifier) = NewSecret();
        var entry = new TicketEntry(
            Guid.NewGuid(),
            verifier,
            request.UserId,
            request.SessionId,
            request.DeviceId,
            now,
            now + WebSocketTicketLifetime);

        _ticketsByVerifier[verifier] = entry;
        return new WebSocketTicketGrant(entry.TicketId, value, entry.IssuedAt, entry.ExpiresAt);
    }

    /// <inheritdoc />
    public WebSocketTicketValidation ConsumeWebSocketTicket(string? presentedValue)
    {
        if (string.IsNullOrEmpty(presentedValue))
        {
            return Refused(WebSocketTicketFailure.Missing);
        }

        var verifier = Verifier(presentedValue);
        var now = _timeProvider.GetUtcNow();

        // TryRemove is the single-use guarantee. It is atomic, so two concurrent presentations of
        // the same ticket cannot both win, and it happens BEFORE the socket is accepted rather than
        // after — a ticket consumed on success only would be replayable by racing the handshake.
        if (!_ticketsByVerifier.TryRemove(verifier, out var entry))
        {
            return Refused(_retiredTickets.TryGetValue(verifier, out var retired) && retired.Consumed
                ? WebSocketTicketFailure.AlreadyUsed
                : _retiredTickets.ContainsKey(verifier)
                    ? WebSocketTicketFailure.Revoked
                    : WebSocketTicketFailure.Unknown);
        }

        _retiredTickets[verifier] = new RetiredEntry(entry.ExpiresAt, true);

        if (now >= entry.ExpiresAt)
        {
            return Refused(WebSocketTicketFailure.Expired);
        }

        return new WebSocketTicketValidation(true, WebSocketTicketFailure.None, entry.UserId, entry.SessionId, entry.DeviceId);

        static WebSocketTicketValidation Refused(WebSocketTicketFailure failure)
            => new(false, failure, Guid.Empty, null, null);
    }

    /// <inheritdoc />
    public int RevokeSession(string sessionId)
        => RevokeCapabilities(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
           + RevokeTickets(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal));

    /// <inheritdoc />
    public int RevokeUser(Guid userId, string? exceptSessionId)
    {
        bool Match(Guid entryUser, string entrySession)
            => entryUser.Equals(userId)
               && (exceptSessionId is null || !string.Equals(entrySession, exceptSessionId, StringComparison.Ordinal));

        return RevokeCapabilities(e => Match(e.UserId, e.SessionId))
               + RevokeTickets(e => Match(e.UserId, e.SessionId));
    }

    /// <inheritdoc />
    public int RevokeDevice(string deviceId)
        => RevokeCapabilities(e => string.Equals(e.DeviceId, deviceId, StringComparison.Ordinal))
           + RevokeTickets(e => string.Equals(e.DeviceId, deviceId, StringComparison.Ordinal));

    /// <inheritdoc />
    public int RevokePlaySession(string playSessionId)
        => RevokeCapabilities(e => string.Equals(e.PlaySessionId, playSessionId, StringComparison.Ordinal));

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetCapabilityIds(string sessionId)
        => _capabilitiesById.Values
            .Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
            .Select(e => e.CapabilityId)
            .ToArray();

    private static string Verifier(string presentedValue)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presentedValue)));

    private (string Value, string Verifier) NewSecret()
    {
        Span<byte> bytes = stackalloc byte[SecretByteCount];
        _randomSecretSource.Fill(bytes);
        var value = Base64UrlEncode(bytes);
        return (value, Verifier(value));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private int RevokeCapabilities(Func<CapabilityEntry, bool> predicate)
    {
        var removed = 0;
        foreach (var entry in _capabilitiesByVerifier.Values.Where(predicate).ToArray())
        {
            if (_capabilitiesByVerifier.TryRemove(entry.Verifier, out _))
            {
                _capabilitiesById.TryRemove(entry.CapabilityId, out _);
                _retiredCapabilities[entry.Verifier] = new RetiredEntry(entry.ExpiresAt, false);
                removed++;
            }
        }

        return removed;
    }

    private int RevokeTickets(Func<TicketEntry, bool> predicate)
    {
        var removed = 0;
        foreach (var entry in _ticketsByVerifier.Values.Where(predicate).ToArray())
        {
            if (_ticketsByVerifier.TryRemove(entry.Verifier, out _))
            {
                _retiredTickets[entry.Verifier] = new RetiredEntry(entry.ExpiresAt, false);
                removed++;
            }
        }

        return removed;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _capabilitiesByVerifier)
        {
            if (now >= pair.Value.ExpiresAt && _capabilitiesByVerifier.TryRemove(pair.Key, out var dead))
            {
                _capabilitiesById.TryRemove(dead.CapabilityId, out _);
            }
        }

        foreach (var pair in _ticketsByVerifier)
        {
            if (now >= pair.Value.ExpiresAt)
            {
                _ticketsByVerifier.TryRemove(pair.Key, out _);
            }
        }

        // Retired entries are held no longer than the credential itself would have lived.
        foreach (var pair in _retiredCapabilities)
        {
            if (now >= pair.Value.OriginalExpiry)
            {
                _retiredCapabilities.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _retiredTickets)
        {
            if (now >= pair.Value.OriginalExpiry)
            {
                _retiredTickets.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record RetiredEntry(DateTimeOffset OriginalExpiry, bool Consumed);

    private sealed class CapabilityEntry
    {
        public CapabilityEntry(
            Guid capabilityId,
            string verifier,
            Guid userId,
            string sessionId,
            string deviceId,
            string playSessionId,
            Guid? itemId,
            string? mediaSourceId,
            IReadOnlyList<PlaybackCapabilityScope> scopes,
            DateTimeOffset issuedAt,
            DateTimeOffset expiresAt)
        {
            CapabilityId = capabilityId;
            Verifier = verifier;
            UserId = userId;
            SessionId = sessionId;
            DeviceId = deviceId;
            PlaySessionId = playSessionId;
            ItemId = itemId;
            MediaSourceId = mediaSourceId;
            Scopes = scopes;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
        }

        public Guid CapabilityId { get; }

        public string Verifier { get; }

        public Guid UserId { get; }

        public string SessionId { get; }

        public string DeviceId { get; }

        public string PlaySessionId { get; }

        public Guid? ItemId { get; }

        public string? MediaSourceId { get; }

        public IReadOnlyList<PlaybackCapabilityScope> Scopes { get; }

        public DateTimeOffset IssuedAt { get; }

        /// <summary>
        /// Gets or sets the expiry. Renewal extends it in place rather than rotating the secret, so
        /// an in-flight segment request holding the value does not fail mid-playback.
        /// </summary>
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed record TicketEntry(
        Guid TicketId,
        string Verifier,
        Guid UserId,
        string SessionId,
        string DeviceId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);
}
