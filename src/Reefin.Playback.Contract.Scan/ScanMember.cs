using System;

namespace Reefin.Playback.Contract.Scan;

/// <summary>
/// One known member of a <see cref="ScanContractLevel"/> (issue #75 slice 75b): its server-owned
/// name as UTF-8 bytes, how the scan treats its value, and - for a container - the child level to
/// descend into.
/// </summary>
/// <remarks>
/// <see cref="Utf8Name"/> is derived from the SAME <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/>
/// the model binder uses (built on the Reefin.Api side and handed in), so a name matches here if and
/// only if the binder would bind it. It is a server-owned name; the scan compares the incoming
/// property name against it with
/// <see cref="System.Text.Json.Utf8JsonReader.ValueTextEquals(System.ReadOnlySpan{byte})"/> and
/// never materializes the incoming name.
/// </remarks>
public sealed class ScanMember
{
    /// <summary>Initializes a new instance of the <see cref="ScanMember"/> class.</summary>
    /// <param name="utf8Name">The member's server-owned JSON name as UTF-8 bytes.</param>
    /// <param name="kind">How the scan treats the member's value.</param>
    /// <param name="child">The child level for a container member, otherwise <see langword="null"/>.</param>
    public ScanMember(ReadOnlyMemory<byte> utf8Name, ScanMemberKind kind, ScanContractLevel? child = null)
    {
        Utf8Name = utf8Name;
        Kind = kind;
        Child = child;
    }

    /// <summary>Gets the member's server-owned JSON name as UTF-8 bytes.</summary>
    public ReadOnlyMemory<byte> Utf8Name { get; }

    /// <summary>Gets how the scan treats the member's value.</summary>
    public ScanMemberKind Kind { get; }

    /// <summary>Gets the child level for a container member, otherwise <see langword="null"/>.</summary>
    public ScanContractLevel? Child { get; }
}
