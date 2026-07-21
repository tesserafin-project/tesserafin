using System;
using System.Collections.Generic;
using System.Text.Json;
using Reefin.Playback.Contract.Diagnostics;

namespace Reefin.Playback.Contract.Scan;

/// <summary>
/// Issue #75 slice 75b: the BOUNDED, SINGLE-PASS structural scan of one raw playback request body.
/// One forward <see cref="Utf8JsonReader"/> pass over an already-bounded in-memory buffer counts the
/// members the client sent that the contract does not declare (per known container) and flags a
/// KNOWN numeric member that arrived string-typed - producing only the closed
/// <see cref="ContractStructuralScan"/>.
/// </summary>
/// <remarks>
/// <para>
/// THE ONE INVARIANT: a client key or value is never materialized. Property names are compared with
/// <see cref="Utf8JsonReader.ValueTextEquals(System.ReadOnlySpan{byte})"/> against server-owned
/// names; an unknown member is counted and its value skipped whole with <see cref="Utf8JsonReader.Skip"/>;
/// a known member's value is inspected only for its token kind and then skipped. There is no call to
/// <c>GetString</c>, <c>CopyString</c>, <c>ValueSpan</c>, <c>ValueSequence</c>, <c>Encoding.GetString</c>,
/// or <c>Enum.Parse</c> anywhere on this path - the project-local <c>BannedSymbols.txt</c> fails the
/// build if one appears.
/// </para>
/// <para>
/// BOUNDEDNESS: the caller reads at most a fixed byte limit and reports whether it was exceeded; a
/// body over the limit is not parsed at all (a truncated
/// parse would be meaningless), only reported as over-limit with its measured size. Within the
/// limit, the reader's own maximum depth bounds nesting - an excessively deep body raises a
/// <see cref="JsonException"/>, which is swallowed here so a malformed or hostile body can never
/// fail the request; whatever was counted before the fault is still returned.
/// </para>
/// </remarks>
public static class PlaybackContractScanner
{
    private static readonly IReadOnlyList<ContractUnknownMemberCount> _noUnknown = Array.Empty<ContractUnknownMemberCount>();
    private static readonly IReadOnlyList<ContractFieldIssue> _noWrongTypes = Array.Empty<ContractFieldIssue>();

    /// <summary>
    /// Scans a bounded request body and returns the closed structural result.
    /// </summary>
    /// <param name="utf8Body">The request body bytes the caller read, up to its size limit.</param>
    /// <param name="root">The contract topology to walk, rooted at the request body object.</param>
    /// <param name="scannedByteCount">
    /// How many bytes the caller actually read from the body. Reported verbatim as
    /// <see cref="ContractStructuralScan.ScannedBodyByteCount"/> - the honest measured size when the
    /// request carried no <c>Content-Length</c>. A count of bytes, never a byte of content.
    /// </param>
    /// <param name="bodyLimitExceeded">
    /// True when the body exceeded the caller's scan size limit. When set, the body is not parsed;
    /// the result reports the flag and the measured size and nothing else.
    /// </param>
    /// <returns>The closed structural scan result.</returns>
    public static ContractStructuralScan Scan(
        ReadOnlySpan<byte> utf8Body,
        ScanContractLevel root,
        long scannedByteCount,
        bool bodyLimitExceeded)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (bodyLimitExceeded)
        {
            return new ContractStructuralScan(0, _noUnknown, _noWrongTypes, scannedByteCount, true);
        }

        var acc = new Accumulator();
        try
        {
            var reader = new Utf8JsonReader(utf8Body);
            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                ScanObject(ref reader, root, acc);
            }

            // A body whose root is not a JSON object (an array, a scalar, empty) attributes nothing:
            // the contract's members can only appear inside its root object. Counts stay 0.
        }
        catch (JsonException)
        {
            // Malformed, or deeper than the reader's maximum depth (the "excessive depth" case). The
            // partial counts gathered before the fault are kept; the scan never throws into the
            // request path, and model binding rejects the same body on its own.
        }

        return acc.Build(scannedByteCount);
    }

    private static void ScanObject(ref Utf8JsonReader reader, ScanContractLevel level, Accumulator acc)
    {
        // reader is positioned at the StartObject token for this level.
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            // Every non-End token at this position is a PropertyName. Match it against the level's
            // server-owned names by bytes - never by materializing the incoming name.
            var member = Match(ref reader, level);

            if (!reader.Read())
            {
                return;
            }

            if (member is null)
            {
                acc.AddUnknown(level.Path);
                reader.Skip();
                continue;
            }

            switch (member.Kind)
            {
                case ScanMemberKind.NumericScalar:
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        acc.AddWrongType(level.Path);
                    }

                    reader.Skip();
                    break;

                case ScanMemberKind.ObjectContainer:
                    if (reader.TokenType == JsonTokenType.StartObject && member.Child is not null)
                    {
                        ScanObject(ref reader, member.Child, acc);
                    }
                    else
                    {
                        reader.Skip();
                    }

                    break;

                case ScanMemberKind.ObjectArray:
                    if (reader.TokenType == JsonTokenType.StartArray && member.Child is not null)
                    {
                        ScanArray(ref reader, member.Child, acc);
                    }
                    else
                    {
                        reader.Skip();
                    }

                    break;

                default:
                    reader.Skip();
                    break;
            }
        }
    }

    private static void ScanArray(ref Utf8JsonReader reader, ScanContractLevel elementLevel, Accumulator acc)
    {
        // reader is positioned at the StartArray token.
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                ScanObject(ref reader, elementLevel, acc);
            }
            else
            {
                // A non-object element (e.g. a codec string in a free-form list) is not a contract
                // object - skip it whole, never read it.
                reader.Skip();
            }
        }
    }

    private static ScanMember? Match(ref Utf8JsonReader reader, ScanContractLevel level)
    {
        var members = level.Members;
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (reader.ValueTextEquals(member.Utf8Name.Span))
            {
                return member;
            }
        }

        return null;
    }

    private sealed class Accumulator
    {
        private readonly Dictionary<ContractPath, int> _unknown = new();
        private readonly HashSet<ContractPath> _wrongTypes = new();

        public int UnknownTotal { get; private set; }

        public void AddUnknown(ContractPath path)
        {
            UnknownTotal++;
            _unknown.TryGetValue(path, out var current);
            _unknown[path] = current + 1;
        }

        public void AddWrongType(ContractPath path) => _wrongTypes.Add(path);

        public ContractStructuralScan Build(long scannedByteCount)
        {
            IReadOnlyList<ContractUnknownMemberCount> unknown;
            if (_unknown.Count == 0)
            {
                unknown = _noUnknown;
            }
            else
            {
                var list = new List<ContractUnknownMemberCount>(_unknown.Count);
                foreach (var pair in _unknown)
                {
                    list.Add(new ContractUnknownMemberCount(pair.Key, pair.Value));
                }

                list.Sort(static (a, b) => ComparePath(a.Path, b.Path));
                unknown = list;
            }

            IReadOnlyList<ContractFieldIssue> wrong;
            if (_wrongTypes.Count == 0)
            {
                wrong = _noWrongTypes;
            }
            else
            {
                var list = new List<ContractFieldIssue>(_wrongTypes.Count);
                foreach (var path in _wrongTypes)
                {
                    list.Add(new ContractFieldIssue(path, ContractIssueCode.WrongType));
                }

                list.Sort(static (a, b) => ComparePath(a.Path, b.Path));
                wrong = list;
            }

            return new ContractStructuralScan(UnknownTotal, unknown, wrong, scannedByteCount, false);
        }

        private static int ComparePath(ContractPath a, ContractPath b)
        {
            var root = ((int)a.Root).CompareTo((int)b.Root);
            if (root != 0)
            {
                return root;
            }

            var branch = ((int)a.Branch).CompareTo((int)b.Branch);
            return branch != 0 ? branch : ((int)a.Leaf).CompareTo((int)b.Leaf);
        }
    }
}
