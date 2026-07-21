namespace Tesserafin.Playback.Contract.Diagnostics;

/// <summary>
/// One closed-vocabulary issue observed for one known contract member (issue #75): WHERE, by
/// server-owned path, and WHAT, by server-owned code. Never why, never with what value.
/// </summary>
/// <param name="Path">The known contract member the issue concerns.</param>
/// <param name="Code">The closed issue code.</param>
public readonly record struct ContractFieldIssue(
    ContractPath Path,
    ContractIssueCode Code);
