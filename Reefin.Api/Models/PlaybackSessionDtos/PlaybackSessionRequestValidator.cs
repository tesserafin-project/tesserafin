using System;
using System.Collections.Generic;
using System.Linq;
using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Validates a <see cref="PlaybackPlanRequestBase"/>'s declared <see cref="ClientCapabilities"/> and
/// <see cref="PlaybackConstraints"/> for internally-inconsistent or unusable input, before it ever
/// reaches the TEMPORARY <c>Reefin.Playback.Dlna.ReverseDlnaAdapter</c> or the legacy pipeline
/// (PR112b).
/// </summary>
/// <remarks>
/// Reefin.Api has no validation-attribute or filter pipeline (no FluentValidation,
/// <see cref="System.ComponentModel.DataAnnotations.IValidatableObject"/>, or custom
/// <see cref="System.ComponentModel.DataAnnotations.ValidationAttribute"/> anywhere in the codebase);
/// the established idiom is throwing <see cref="ArgumentException"/>, which
/// <c>Reefin.Api.Middleware.ExceptionMiddleware</c> maps to 400 automatically - the same pattern this
/// controller already uses for 404 (<c>ResourceNotFoundException</c>, uncaught). This only validates
/// what is actually verifiable from the <see cref="Reefin.Playback.Decision"/> vocabulary itself:
/// there is no "forbidden codec" concept in that vocabulary to check a codec against, but a codec
/// declared decodable with an explicit zero-or-negative bitrate cap is exactly that same
/// declared-supported-yet-unusable contradiction in the terms the domain actually has.
/// </remarks>
public static class PlaybackSessionRequestValidator
{
    /// <summary>
    /// Validates a request's capabilities and constraints, throwing <see cref="ArgumentException"/>
    /// with every violation found (not just the first) if any are invalid.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <exception cref="ArgumentException">The request's capabilities or constraints are invalid.</exception>
    public static void Validate(PlaybackPlanRequestBase request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        ValidateCapabilities(request.Capabilities, errors);
        ValidateConstraints(request.Constraints, errors);

        // Issue #43: validated here so BOTH the POST and the PUT get it, and neither can forget to.
        // Only a value that IS supplied and IS malformed produces an error - a third-party client
        // that omits the field entirely must keep playing exactly as before.
        PlaybackAttemptIdValidator.Validate(request.PlaybackAttemptId, errors);

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors));
        }
    }

    private static void ValidateCapabilities(ClientCapabilities? capabilities, List<string> errors)
    {
        if (capabilities is null)
        {
            errors.Add("capabilities is required.");
            return;
        }

        var decode = capabilities.Decode;
        if (decode is null)
        {
            errors.Add("capabilities.decode is required.");
            return;
        }

        var directPlayProfiles = decode.DirectPlayProfiles ?? [];
        var videoCodecs = decode.VideoCodecs ?? [];
        var audioCodecs = decode.AudioCodecs ?? [];

        if (directPlayProfiles.Count == 0 && videoCodecs.Count == 0 && audioCodecs.Count == 0)
        {
            errors.Add("capabilities.decode must declare at least one direct-play profile, video codec, or audio codec - a client that can decode nothing cannot play anything.");
        }

        for (var i = 0; i < videoCodecs.Count; i++)
        {
            if (videoCodecs[i].MaxBitrate is <= 0)
            {
                errors.Add($"capabilities.decode.videoCodecs[{i}].maxBitrate must be positive if specified - a non-positive cap declares the codec supported and simultaneously unusable.");
            }
        }

        for (var i = 0; i < audioCodecs.Count; i++)
        {
            if (audioCodecs[i].MaxBitrate is <= 0)
            {
                errors.Add($"capabilities.decode.audioCodecs[{i}].maxBitrate must be positive if specified - a non-positive cap declares the codec supported and simultaneously unusable.");
            }
        }

        var duplicateVideoCodecIndex = FirstDuplicateIndex(videoCodecs.Select(c => c.Codec));
        if (duplicateVideoCodecIndex >= 0)
        {
            errors.Add($"capabilities.decode.videoCodecs declares a duplicate codec at index {duplicateVideoCodecIndex}.");
        }

        var duplicateAudioCodecIndex = FirstDuplicateIndex(audioCodecs.Select(c => c.Codec));
        if (duplicateAudioCodecIndex >= 0)
        {
            errors.Add($"capabilities.decode.audioCodecs declares a duplicate codec at index {duplicateAudioCodecIndex}.");
        }

        var outputProfiles = capabilities.OutputProfiles ?? [];
        for (var i = 0; i < outputProfiles.Count; i++)
        {
            var profile = outputProfiles[i];

            if (profile.MaxVideoBitrate is <= 0)
            {
                errors.Add($"capabilities.outputProfiles[{i}].maxVideoBitrate must be positive if specified.");
            }

            if (profile.MaxAudioBitrate is <= 0)
            {
                errors.Add($"capabilities.outputProfiles[{i}].maxAudioBitrate must be positive if specified.");
            }

            if (profile.MaxAudioChannels is <= 0)
            {
                errors.Add($"capabilities.outputProfiles[{i}].maxAudioChannels must be positive if specified.");
            }
        }
    }

    /// <summary>
    /// Returns the index of the first value that repeats an earlier one (case-insensitively), or
    /// <c>-1</c> when there is no repeat. Issue #79: the index is the only thing reported, because it
    /// is computed by the server; the repeated value itself is client-supplied free-form text and
    /// must never reach an exception message, a response body, or a log sink.
    /// </summary>
    private static int FirstDuplicateIndex(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var value in values)
        {
            // A null codec name is not a domain concept, but the client controls the JSON: treat it
            // as its own bucket rather than letting the comparer throw.
            if (!seen.Add(value ?? string.Empty))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static void ValidateConstraints(PlaybackConstraints? constraints, List<string> errors)
    {
        if (constraints is null)
        {
            errors.Add("constraints is required.");
            return;
        }

        if (constraints.MaxBitrate is <= 0)
        {
            errors.Add("constraints.maxBitrate must be positive if specified.");
        }

        if (constraints.MaxAudioChannels is <= 0)
        {
            errors.Add("constraints.maxAudioChannels must be positive if specified.");
        }

        if (constraints.StartTimeTicks < 0)
        {
            errors.Add("constraints.startTimeTicks must not be negative.");
        }

        if (!constraints.AllowDirectPlay && !constraints.AllowDirectStream && !constraints.AllowTranscoding)
        {
            errors.Add("constraints must allow at least one playback method (direct play, direct stream, or transcoding) - forbidding all three leaves no viable plan.");
        }
    }
}
