using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// The question an administrator is asking the remote-access diagnostics.
/// </summary>
/// <remarks>
/// A dedicated wire type. The internal <c>PublicationPolicyInput</c> record is never bound
/// directly: binding it would publish an implementation record as the contract, and every later
/// change to it — a field rename, an added parameter, a nullability change — would become a
/// silent, unreviewed contract change.
///
/// The hostname travels in the body and nowhere else. It is not a route value and not a query
/// parameter, because a hostname in a URL is written to access logs, proxy logs and browser
/// history by parties that never agreed to hold it.
///
/// Both family policies carry <see cref="NamedRemoteAccessPublicationPolicyConverter"/> so that the
/// runtime accepts exactly the vocabulary the contract publishes - names, never ordinals. The
/// converter is attached here rather than configured globally: the global JSON options belong to
/// every API in the server, and this endpoint does not get to change them.
/// </remarks>
public sealed class RemoteAccessDiagnosticsRequestDto
{
    /// <summary>
    /// Gets or sets the hostname the administrator proposes to publish, if any.
    /// </summary>
    /// <remarks>
    /// Optional, and absence is a valid question rather than an error: the report answers it with
    /// the <c>HostnameNotProvided</c> finding.
    ///
    /// A syntactically invalid hostname is likewise NOT a transport error. It is diagnostic
    /// evidence — the administrator typed something that cannot be a hostname, which is exactly
    /// the kind of thing this endpoint exists to tell them — so it produces the
    /// <c>HostnameSyntacticallyInvalid</c> finding with HTTP 200, and the resolver is never
    /// called with it.
    /// </remarks>
    public string? Hostname { get; set; }

    /// <summary>
    /// Gets or sets the caller's intention for IPv4. Required.
    /// </summary>
    [Required]
    [JsonConverter(typeof(NamedRemoteAccessPublicationPolicyConverter))]
    public RemoteAccessPublicationPolicy? IPv4Policy { get; set; }

    /// <summary>
    /// Gets or sets the caller's intention for IPv6. Required.
    /// </summary>
    [Required]
    [JsonConverter(typeof(NamedRemoteAccessPublicationPolicyConverter))]
    public RemoteAccessPublicationPolicy? IPv6Policy { get; set; }
}
