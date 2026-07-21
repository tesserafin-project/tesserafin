using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests;

/// <summary>
/// Issue #75's central proof, stated as a single self-contained claim: when a real loss happens in
/// the TYPED before/after comparison, the diagnostic reports THAT a loss happened and WHERE - by
/// server-owned <see cref="ContractPath"/> - and its cardinality, and it exposes NOTHING of the lost
/// value itself.
/// </summary>
/// <remarks>
/// <para>
/// SYNTHETIC, not adapter-driven. The "before" and "after" capabilities are both constructed by
/// hand here, with the "after" deliberately missing entries the "before" declared. This decouples
/// the proof from <c>ReverseDlnaAdapter</c>/<c>DlnaPlaybackAdapter</c> behaviour (which
/// <see cref="ContractMappingDiagnosticFactoryTests"/> exercises separately): the claim under test
/// is about what the diagnostic can and cannot carry, and must hold for ANY loss, however produced.
/// The declared codec names are distinctive on purpose so that <see cref="NoStringIsReachable"/>
/// would catch them if the diagnostic leaked one.
/// </para>
/// </remarks>
public sealed class ContractMappingCentralProofTests
{
    private const string LostVideoCodecA = "av01-lost-sentinel";
    private const string LostVideoCodecB = "vp9-lost-sentinel";

    /// <summary>
    /// The whole issue in one test: a collection that shrank AND a scalar that was dropped both
    /// surface as deltas naming only their path and cardinalities, and the produced instance carries
    /// no string anywhere - so no lost value can have escaped into it.
    /// </summary>
    [Fact]
    public void SyntheticLoss_ReportsCardinalityAndPath_NeverTheLostValue()
    {
        // BEFORE: three declared video codecs and a declared HLS capability.
        var declared = new ClientCapabilities(
            new DecodeCapabilities(
                DirectPlayProfiles: [],
                VideoCodecs:
                [
                    new VideoCodecCapability("h264", ["high"], 41.0, 8, ["SDR"], new Resolution(1920, 1080), 8_000_000),
                    new VideoCodecCapability(LostVideoCodecA, ["main"], null, null, ["SDR"], null, null),
                    new VideoCodecCapability(LostVideoCodecB, ["main"], null, null, ["SDR"], null, null),
                ],
                AudioCodecs: [],
                SubtitleDelivery: [],
                SupportsHls: true,
                SupportsDash: false),
            []);

        // AFTER: only one video codec survives, and the HLS capability is gone. A hand-built loss,
        // no adapter involved.
        var mapped = new ClientCapabilities(
            new DecodeCapabilities(
                DirectPlayProfiles: [],
                VideoCodecs:
                [
                    new VideoCodecCapability("h264", ["high"], 41.0, 8, ["SDR"], new Resolution(1920, 1080), 8_000_000),
                ],
                AudioCodecs: [],
                SubtitleDelivery: [],
                SupportsHls: false,
                SupportsDash: false),
            []);

        var diagnostic = ContractMappingDiagnosticFactory.Create(declared, mapped, 4096);
        Assert.NotNull(diagnostic);

        // (1) The collection loss: CountBefore > CountAfter, named only by its path.
        var video = Assert.Single(diagnostic!.Deltas, d => d.Path == ContractPath.DecodeVideoCodecs);
        Assert.True(video.CountBefore > video.CountAfter, "expected the shrunk collection to report CountBefore > CountAfter");
        Assert.Equal(3, video.CountBefore);
        Assert.Equal(1, video.CountAfter);

        // (2) The scalar loss: PresentBefore && !PresentAfter, named only by its path.
        var hls = Assert.Single(diagnostic.Deltas, d => d.Path == ContractPath.DecodeSupportsHls);
        Assert.True(hls.PresentBefore && !hls.PresentAfter, "expected the dropped scalar to report PresentBefore && !PresentAfter");

        // (3) The diagnostic exposes ONLY paths and cardinalities: every leaf reachable from it is a
        // bool, an integer, or an enum - there is structurally nowhere for a value to sit.
        AssertOnlyPathsAndCardinalities(diagnostic);

        // (4) No lost value escaped: not a single string is reachable from the produced instance, so
        // the distinctive lost codec names cannot be inside it even in a mangled or partial form.
        NoStringIsReachable(diagnostic);
    }

    /// <summary>
    /// A <see cref="ContractMappingDelta"/> is made only of a <see cref="ContractPath"/> and numeric
    /// or boolean cardinality/presence fields - proven against the live type, so a future field of
    /// any other kind trips this immediately.
    /// </summary>
    private static void AssertOnlyPathsAndCardinalities(ContractMappingDiagnostic diagnostic)
    {
        var closedAssembly = typeof(ContractMappingDiagnostic).Assembly;

        foreach (var value in ReachableValues(diagnostic))
        {
            var type = value.GetType();

            // Strings are handled by the value-absence proof; do not double-report them here.
            if (value is string)
            {
                continue;
            }

            // A leaf: it must be a boolean or an integer (a cardinality/presence flag), or an enum
            // that this assembly's closed vocabulary owns (a path segment or issue code).
            if (type.IsPrimitive || type.IsEnum)
            {
                var isCardinality = type == typeof(bool) || type == typeof(int) || type == typeof(long);
                var isClosedEnum = type.IsEnum && type.Assembly == closedAssembly;

                Assert.True(
                    isCardinality || isClosedEnum,
                    $"Issue #75: a leaf of type {type} is reachable from the diagnostic - only booleans, integers and closed-vocabulary enums may be.");
                continue;
            }

            // A list/collection is just a container we walk through.
            if (value is IEnumerable)
            {
                continue;
            }

            // Any remaining composite (a record or struct) must be one this assembly declares - no
            // foreign object, DTO, or domain type may be reachable from the diagnostic at all.
            Assert.True(
                type.Assembly == closedAssembly,
                $"Issue #75: a composite of foreign type {type} is reachable from the diagnostic - only the closed-vocabulary assembly's own types may be.");
        }
    }

    /// <summary>
    /// Not one string is reachable from the produced diagnostic. This is the value-absence proof: a
    /// lost codec name, container or profile could only escape as text, and there is no text here.
    /// </summary>
    private static void NoStringIsReachable(ContractMappingDiagnostic diagnostic)
    {
        foreach (var value in ReachableValues(diagnostic))
        {
            Assert.False(
                value is string,
                $"Issue #75: a string ('{value}') is reachable from the diagnostic - the closure must carry no value a client sent.");
        }
    }

    /// <summary>
    /// Walks the object graph of a produced diagnostic instance and yields every non-null leaf and
    /// composite value reached through public instance properties and, for lists, their items.
    /// </summary>
    private static IEnumerable<object> ReachableValues(object root)
    {
        var stack = new Stack<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            // Value types are boxed afresh on every read, so identity-dedup only helps (and only
            // makes sense) for reference types; guard against cycles among those.
            if (!current.GetType().IsValueType && !seen.Add(current))
            {
                continue;
            }

            yield return current;

            if (current is string)
            {
                // A string is a leaf here - do not recurse into its chars. It is reported as a
                // violation by the caller; we just must not walk into it.
                continue;
            }

            if (current is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is not null)
                    {
                        stack.Push(item);
                    }
                }

                continue;
            }

            var type = current.GetType();
            if (type.IsPrimitive || type.IsEnum)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0
                    || string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
                {
                    continue;
                }

                var child = property.GetValue(current);
                if (child is not null)
                {
                    stack.Push(child);
                }
            }
        }
    }
}
