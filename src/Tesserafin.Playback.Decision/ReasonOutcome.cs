namespace Tesserafin.Playback.Decision;

/// <summary>
/// The result a <see cref="ReasonNode"/> records for its <see cref="ReasonSubject"/>.
/// </summary>
public enum ReasonOutcome
{
    /// <summary>
    /// The subject was evaluated and rejected.
    /// </summary>
    Rejected,

    /// <summary>
    /// The subject was evaluated and accepted.
    /// </summary>
    Accepted,

    /// <summary>
    /// The subject was selected as part of the final decision.
    /// </summary>
    Chosen,
}
