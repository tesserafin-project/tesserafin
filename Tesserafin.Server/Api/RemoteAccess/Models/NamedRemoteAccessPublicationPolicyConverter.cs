using System.Text.Json.Serialization;

namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// Reads and writes <see cref="RemoteAccessPublicationPolicy"/> as a name, and only as a name.
/// </summary>
/// <remarks>
/// The published contract offers three named values for each family policy. The server's global
/// JSON configuration registers <c>JsonStringEnumConverter</c> with integer values still allowed,
/// so without this the runtime would also accept <c>0</c>, <c>1</c> and <c>2</c> — a second,
/// unpublished vocabulary in which a caller reading the contract could not know that <c>1</c> means
/// "do not publish". Two vocabularies for one field is how a caller ends up publishing a family it
/// meant to keep private.
///
/// ENDPOINT-SCOPED ON PURPOSE. This is attached to the two properties of
/// <see cref="RemoteAccessDiagnosticsRequestDto"/> with <see cref="JsonConverterAttribute"/> and
/// nowhere else: the global setting is shared by every API in the server and is not R1-P's to
/// change. It also changes nothing about how the value is written, so the response and the
/// generated contract are unaffected.
/// </remarks>
public sealed class NamedRemoteAccessPublicationPolicyConverter
    : JsonStringEnumConverter<RemoteAccessPublicationPolicy>
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="NamedRemoteAccessPublicationPolicyConverter"/> class.
    /// </summary>
    public NamedRemoteAccessPublicationPolicyConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
