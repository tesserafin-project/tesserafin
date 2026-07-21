using System.Collections.Generic;
using Tesserafin.Playback.Contract.Diagnostics;

namespace Tesserafin.Playback.Contract.Scan;

/// <summary>
/// One object level of the playback request contract the scan walks (issue #75 slice 75b): the
/// server-owned <see cref="ContractPath"/> an unknown or wrong-typed member inside it is attributed
/// to, and the set of members the contract declares at this level.
/// </summary>
/// <remarks>
/// The tree of levels IS the server's contract topology - a compile-time, server-owned shape, with
/// member NAMES sourced from the binder's own metadata. Nothing about it comes from a request. A
/// member the client sent that matches no <see cref="Members"/> entry here is, by definition,
/// unknown, and is counted against <see cref="Path"/> without being named.
/// </remarks>
public sealed class ScanContractLevel
{
    /// <summary>Initializes a new instance of the <see cref="ScanContractLevel"/> class.</summary>
    /// <param name="path">The server-owned path this level is named by for attribution.</param>
    /// <param name="members">The members the contract declares at this level.</param>
    public ScanContractLevel(ContractPath path, IReadOnlyList<ScanMember> members)
    {
        Path = path;
        Members = members;
    }

    /// <summary>Gets the server-owned path unknown/wrong-typed members at this level are attributed to.</summary>
    public ContractPath Path { get; }

    /// <summary>Gets the members the contract declares at this level.</summary>
    public IReadOnlyList<ScanMember> Members { get; }
}
