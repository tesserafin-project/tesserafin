namespace Reefin.Playback.Contract.Diagnostics;

/// <summary>
/// The before/after comparison of ONE known contract member across the request mapping
/// (issue #75, "comparaison avant/après uniquement pour les membres déjà définis dans le
/// contrat"). This is the signal Option 1 actually delivers: it separates case (b) - the client
/// declared something and the mapping lost it - from case (c) - the mapping kept everything and the
/// decision engine is what changed.
/// </summary>
/// <remarks>
/// Counts and presence flags only. A delta names the affected <see cref="ContractPath"/> and says
/// HOW MUCH was lost; it can never say WHAT was lost, because nothing in this type can hold a
/// value. That is the deliberate trade: an operator learns "the client declared 4 video codec
/// entries and the mapping kept 2", never which two.
/// </remarks>
/// <param name="Path">The known contract member this delta concerns.</param>
/// <param name="PresentBefore">Whether the member was declared by the client.</param>
/// <param name="PresentAfter">Whether the member survived into the mapped capabilities.</param>
/// <param name="CountBefore">
/// How many entries the client declared for a collection member, or 0 for a scalar member (whose
/// signal is <see cref="PresentBefore"/>/<see cref="PresentAfter"/> instead).
/// </param>
/// <param name="CountAfter">How many entries survived the mapping, under the same convention as <see cref="CountBefore"/>.</param>
public readonly record struct ContractMappingDelta(
    ContractPath Path,
    bool PresentBefore,
    bool PresentAfter,
    int CountBefore,
    int CountAfter);
