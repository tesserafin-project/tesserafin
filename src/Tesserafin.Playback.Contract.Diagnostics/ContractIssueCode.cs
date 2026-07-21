namespace Tesserafin.Playback.Contract.Diagnostics;

/// <summary>
/// The closed error vocabulary issue #75 allows for a KNOWN contract member. Every member of this
/// enum is server-owned; none of them carries, encodes, or implies any value the client sent.
/// </summary>
/// <remarks>
/// Honesty about what this iteration actually emits (issue #75, Option 1 - no structural scan of
/// the raw body):
/// <list type="bullet">
/// <item><description>
/// <see cref="Missing"/> and <see cref="Truncated"/> are the only codes with a semantically correct
/// source available today, and even they are reached through the mapping delta rather than through
/// a body scan - which is why this iteration emits an empty
/// <see cref="ContractMappingDiagnostic.FieldIssues"/> in practice and reports its signal as
/// <see cref="ContractMappingDelta"/> instead.
/// </description></item>
/// <item><description>
/// <see cref="WrongType"/> is not observable: by the time anything downstream of the model binder
/// can look, the binder has already coerced (or rejected) the value. Asserting WrongType for a
/// value the binder successfully coerced would be a fabricated diagnostic.
/// </description></item>
/// <item><description>
/// <see cref="OutOfRange"/> is almost never observable either: a value out of the contract's range
/// is rejected by request validation BEFORE the shadow publication point, so no diagnostic is
/// created for it at all.
/// </description></item>
/// <item><description>
/// <see cref="UnsupportedValue"/> is NEVER emitted, by design and not by omission. The only
/// server-side signal that looks like a source for it - <c>IMediaEncoder.SupportsDecoder</c>/
/// <c>SupportsEncoder</c> - answers a different question: whether THIS server can decode/encode a
/// codec says nothing about whether a client-declared codec is in the contract's vocabulary, nor
/// about whether the CLIENT can read it. Using it as a proxy produces false diagnostics, most
/// obviously for a codec the server can DirectPlay to a client without being able to decode it
/// itself. The free-form codec/container/profile members are therefore never flagged, and this
/// iteration explicitly does NOT claim to detect a well-formed misspelling such as <c>av01</c>.
/// The member is retained so the vocabulary stays stable when a semantically correct source exists.
/// </description></item>
/// </list>
/// </remarks>
public enum ContractIssueCode
{
    /// <summary>No issue. The default, so a defaulted <see cref="ContractFieldIssue"/> asserts nothing.</summary>
    None = 0,

    /// <summary>A member the contract requires was not present.</summary>
    Missing = 1,

    /// <summary>A member was present with a type the contract does not declare for it. Never emitted - see the type remarks.</summary>
    WrongType = 2,

    /// <summary>A member was present, well-typed, and outside the range the contract declares. Practically never emitted - see the type remarks.</summary>
    OutOfRange = 3,

    /// <summary>A member carried a value outside the contract's closed vocabulary. NEVER emitted in this iteration - see the type remarks.</summary>
    UnsupportedValue = 4,

    /// <summary>A member's content did not survive intact.</summary>
    Truncated = 5,
}
