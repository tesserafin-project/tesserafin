namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// How the caller intends to publish one IP family.
/// </summary>
/// <remarks>
/// Three explicit states, never a two-state boolean with a default.
///
/// The internal engine models this as <c>bool?</c>, where <c>null</c> means "the caller did not
/// say". A JSON boolean cannot express that: an absent field would deserialize to <c>false</c>,
/// and "the caller said nothing" would silently become "the caller said do not publish" — a
/// different question, with a different answer, reported as though it had been asked. So the wire
/// carries three named values and the field is required.
///
/// <see cref="Unspecified"/> is a real answer, not a default. There is deliberately no value whose
/// omission could be read as permission to publish.
/// </remarks>
public enum RemoteAccessPublicationPolicy
{
    /// <summary>
    /// The caller expressed no intention for this family. Maps to the internal <c>null</c>.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The caller does not intend to publish this family. Maps to the internal <c>false</c>.
    /// </summary>
    DoNotPublish = 1,

    /// <summary>
    /// The caller intends to publish this family. Maps to the internal <c>true</c>.
    /// </summary>
    Publish = 2
}
