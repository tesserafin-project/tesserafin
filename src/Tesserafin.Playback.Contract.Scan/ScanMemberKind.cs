using Tesserafin.Playback.Contract.Diagnostics;

namespace Tesserafin.Playback.Contract.Scan;

/// <summary>
/// How the scan treats a KNOWN member's value once its name has matched (issue #75 slice 75b).
/// Every case reads only the value's JSON token kind and then skips the value whole - it never
/// enters, decodes, or copies the value's bytes.
/// </summary>
public enum ScanMemberKind
{
    /// <summary>
    /// A known scalar the scan makes no type claim about (string/enum/guid/bool). Its value is
    /// skipped; no <see cref="ContractIssueCode.WrongType"/> is ever asserted for it, because the
    /// lenient converters in the binder's options make most token kinds legitimately bindable and
    /// asserting otherwise would fabricate a diagnostic.
    /// </summary>
    Scalar = 0,

    /// <summary>
    /// A known member the contract declares as numeric. If its value arrives as a JSON string the
    /// scan records a <see cref="ContractIssueCode.WrongType"/> against the enclosing level's path
    /// (the binder still coerces it via <c>AllowReadingFromString</c>, so the request reaches the
    /// shadow publication point). The string's content is never read.
    /// </summary>
    NumericScalar = 1,

    /// <summary>A known member whose value is another contract object the scan descends into when it is a JSON object.</summary>
    ObjectContainer = 2,

    /// <summary>A known member whose value is a JSON array of contract objects the scan descends into element by element.</summary>
    ObjectArray = 3,
}
