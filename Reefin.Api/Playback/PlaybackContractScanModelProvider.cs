using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Extensions.Json;
using Reefin.Playback.Contract.Diagnostics;
using Reefin.Playback.Contract.Scan;
using Reefin.Playback.Decision;

namespace Reefin.Api.Playback;

/// <summary>
/// Issue #75 slice 75b: builds - once, and caches - the <see cref="ScanContractLevel"/> topology the
/// <see cref="PlaybackContractScanFilter"/> hands to <see cref="PlaybackContractScanner"/>.
/// </summary>
/// <remarks>
/// <para>
/// The member NAMES come from the SAME <see cref="JsonTypeInfo"/> the model binder resolves the
/// request through: <see cref="JsonDefaults.PascalCaseOptions"/> is the canonical options object the
/// MVC JSON pipeline is configured to mirror (see <see cref="JsonDefaults"/>'s own note and
/// <c>ApiServiceCollectionExtensions.AddReefinApi</c>, which copies its naming policy and converters
/// verbatim). A name therefore matches in the scan if and only if it would bind - the scan can never
/// count a member the binder would have accepted as "unknown", nor miss one it would have rejected.
/// The shape of the tree (which member descends where, and the <see cref="ContractPath"/> each level
/// is named by) is a compile-time, server-owned decision; only the names are read from metadata.
/// </para>
/// <para>
/// Numeric-ness (which drives the <see cref="ContractIssueCode.WrongType"/> signal) is read from the
/// same metadata's <see cref="JsonPropertyInfo.PropertyType"/>, so the set of members a JSON string
/// is "wrong" for stays in lockstep with the contract without a second hand-maintained list.
/// </para>
/// </remarks>
public sealed class PlaybackContractScanModelProvider
{
    private readonly object _gate = new();
    private ScanContractLevel? _createRoot;
    private ScanContractLevel? _replaceRoot;

    /// <summary>
    /// Gets the cached contract level rooted at <see cref="CreatePlaybackSessionRequest"/> (the POST
    /// body), building it on first access. Thread-safe: the tree is immutable once built.
    /// </summary>
    public ScanContractLevel CreateRoot
    {
        get
        {
            EnsureBuilt();
            return _createRoot!;
        }
    }

    /// <summary>
    /// Gets the cached contract level rooted at <see cref="ReplacePlaybackSessionRequest"/> (the PUT
    /// body), building it on first access. It shares the whole capabilities subtree with
    /// <see cref="CreateRoot"/> and differs only at the request root, where a PUT declares no
    /// <c>PlaySessionId</c> - so a "PlaySessionId" a PUT sends is honestly counted as unknown, exactly
    /// as the binder would treat it.
    /// </summary>
    public ScanContractLevel ReplaceRoot
    {
        get
        {
            EnsureBuilt();
            return _replaceRoot!;
        }
    }

    private void EnsureBuilt()
    {
        if (_createRoot is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_createRoot is not null)
            {
                return;
            }

            var options = JsonDefaults.PascalCaseOptions;

            // Leaves first: codec/profile entries carry the numeric members WrongType is observable
            // on. No container overrides - every member is auto-classified numeric-or-scalar from
            // metadata.
            var videoCodec = BuildLevel(options, typeof(VideoCodecCapability), ContractPath.DecodeVideoCodecs, null);
            var audioCodec = BuildLevel(options, typeof(AudioCodecCapability), ContractPath.DecodeAudioCodecs, null);
            var outputProfile = BuildLevel(options, typeof(PlaybackOutputProfile), ContractPath.OutputProfiles, null);

            var decode = BuildLevel(options, typeof(DecodeCapabilities), ContractPath.Decode, new Dictionary<string, ChildOverride>(StringComparer.Ordinal)
            {
                [nameof(DecodeCapabilities.VideoCodecs)] = new(ScanMemberKind.ObjectArray, videoCodec),
                [nameof(DecodeCapabilities.AudioCodecs)] = new(ScanMemberKind.ObjectArray, audioCodec),
            });

            var capabilities = BuildLevel(options, typeof(ClientCapabilities), ContractPath.Capabilities, new Dictionary<string, ChildOverride>(StringComparer.Ordinal)
            {
                [nameof(ClientCapabilities.Decode)] = new(ScanMemberKind.ObjectContainer, decode),
                [nameof(ClientCapabilities.OutputProfiles)] = new(ScanMemberKind.ObjectArray, outputProfile),
            });

            var capabilitiesOverride = new Dictionary<string, ChildOverride>(StringComparer.Ordinal)
            {
                [nameof(CreatePlaybackSessionRequest.Capabilities)] = new(ScanMemberKind.ObjectContainer, capabilities),
            };

            _replaceRoot = BuildLevel(options, typeof(ReplacePlaybackSessionRequest), ContractPath.Request, capabilitiesOverride);
            _createRoot = BuildLevel(options, typeof(CreatePlaybackSessionRequest), ContractPath.Request, capabilitiesOverride);
        }
    }

    private static ScanContractLevel BuildLevel(
        JsonSerializerOptions options,
        Type type,
        ContractPath path,
        IReadOnlyDictionary<string, ChildOverride>? overrides)
    {
        var typeInfo = options.GetTypeInfo(type);
        var members = new List<ScanMember>(typeInfo.Properties.Count);

        foreach (var property in typeInfo.Properties)
        {
            var utf8Name = Encoding.UTF8.GetBytes(property.Name);

            if (overrides is not null && overrides.TryGetValue(property.Name, out var over))
            {
                members.Add(new ScanMember(utf8Name, over.Kind, over.Child));
                continue;
            }

            var kind = IsNumeric(property.PropertyType) ? ScanMemberKind.NumericScalar : ScanMemberKind.Scalar;
            members.Add(new ScanMember(utf8Name, kind));
        }

        return new ScanContractLevel(path, members);
    }

    private static bool IsNumeric(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(int)
            || t == typeof(long)
            || t == typeof(short)
            || t == typeof(byte)
            || t == typeof(sbyte)
            || t == typeof(uint)
            || t == typeof(ulong)
            || t == typeof(ushort)
            || t == typeof(double)
            || t == typeof(float)
            || t == typeof(decimal);
    }

    private readonly struct ChildOverride
    {
        public ChildOverride(ScanMemberKind kind, ScanContractLevel child)
        {
            Kind = kind;
            Child = child;
        }

        public ScanMemberKind Kind { get; }

        public ScanContractLevel Child { get; }
    }
}
