using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Api.Playback;
using Tesserafin.Extensions.Json;
using Tesserafin.Playback.Contract.Scan;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Api.Tests.Playback;

/// <summary>
/// Issue #75 slice 75b: the scan model's known member names come from the SAME metadata the model
/// binder resolves the request through (<see cref="JsonDefaults.PascalCaseOptions"/>, which the MVC
/// JSON pipeline mirrors). This is the enumerated "known member names sourced from the same
/// JsonTypeInfo the binder uses" constraint, and the guard that catches a future member being added
/// to a DTO without the scan learning its real bound name.
/// </summary>
public sealed class PlaybackContractScanModelProviderTests
{
    private static readonly PlaybackContractScanModelProvider _provider = new();

    private static ISet<string> LevelNames(ScanContractLevel level) =>
        level.Members.Select(m => Encoding.UTF8.GetString(m.Utf8Name.Span)).ToHashSet(StringComparer.Ordinal);

    private static ISet<string> BinderNames(Type type) =>
        JsonDefaults.PascalCaseOptions.GetTypeInfo(type).Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    private static ScanContractLevel Child(ScanContractLevel level, string member) =>
        level.Members.Single(m => Encoding.UTF8.GetString(m.Utf8Name.Span) == member).Child!;

    [Fact]
    public void CreateRoot_MemberNames_MatchBinderMetadata()
    {
        Assert.Equal(BinderNames(typeof(CreatePlaybackSessionRequest)), LevelNames(_provider.CreateRoot));
    }

    [Fact]
    public void ReplaceRoot_MemberNames_MatchBinderMetadata()
    {
        Assert.Equal(BinderNames(typeof(ReplacePlaybackSessionRequest)), LevelNames(_provider.ReplaceRoot));
    }

    [Fact]
    public void CapabilitiesLevel_MemberNames_MatchBinderMetadata()
    {
        var capabilities = Child(_provider.CreateRoot, "Capabilities");
        Assert.Equal(BinderNames(typeof(ClientCapabilities)), LevelNames(capabilities));
    }

    [Fact]
    public void DecodeLevel_MemberNames_MatchBinderMetadata()
    {
        var decode = Child(Child(_provider.CreateRoot, "Capabilities"), "Decode");
        Assert.Equal(BinderNames(typeof(DecodeCapabilities)), LevelNames(decode));
    }

    [Fact]
    public void VideoCodecLevel_MemberNames_MatchBinderMetadata()
    {
        var videoCodecs = Child(Child(Child(_provider.CreateRoot, "Capabilities"), "Decode"), "VideoCodecs");
        Assert.Equal(BinderNames(typeof(VideoCodecCapability)), LevelNames(videoCodecs));
    }

    [Fact]
    public void MemberNames_ArePascalCase_MatchingTheNullNamingPolicyBinderUses()
    {
        // The binder's PropertyNamingPolicy is null (PascalCase = CLR names); the scan must compare
        // against those exact names, not a camelCased form.
        Assert.Contains("Capabilities", LevelNames(_provider.CreateRoot));
        var decode = Child(Child(_provider.CreateRoot, "Capabilities"), "Decode");
        Assert.Contains("VideoCodecs", LevelNames(decode));
        var videoCodecs = Child(decode, "VideoCodecs");
        Assert.Contains("MaxBitrate", LevelNames(videoCodecs));
    }
}
