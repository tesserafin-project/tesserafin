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

        foreach (var codec in videoCodecs)
        {
            if (codec.MaxBitrate is <= 0)
            {
                errors.Add($"capabilities.decode.videoCodecs['{codec.Codec}'].maxBitrate must be positive if specified - a non-positive cap declares the codec supported and simultaneously unusable.");
            }
        }

        foreach (var codec in audioCodecs)
        {
            if (codec.MaxBitrate is <= 0)
            {
                errors.Add($"capabilities.decode.audioCodecs['{codec.Codec}'].maxBitrate must be positive if specified - a non-positive cap declares the codec supported and simultaneously unusable.");
            }
        }

        var duplicateVideoCodec = videoCodecs.Select(c => c.Codec).GroupBy(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateVideoCodec is not null)
        {
            errors.Add($"capabilities.decode.videoCodecs declares '{duplicateVideoCodec.Key}' more than once.");
        }

        var duplicateAudioCodec = audioCodecs.Select(c => c.Codec).GroupBy(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateAudioCodec is not null)
        {
            errors.Add($"capabilities.decode.audioCodecs declares '{duplicateAudioCodec.Key}' more than once.");
        }

        foreach (var profile in capabilities.OutputProfiles ?? [])
        {
            if (profile.MaxVideoBitrate is <= 0)
            {
                errors.Add($"capabilities.outputProfiles['{profile.Container}'].maxVideoBitrate must be positive if specified.");
            }

            if (profile.MaxAudioBitrate is <= 0)
            {
                errors.Add($"capabilities.outputProfiles['{profile.Container}'].maxAudioBitrate must be positive if specified.");
            }

            if (profile.MaxAudioChannels is <= 0)
            {
                errors.Add($"capabilities.outputProfiles['{profile.Container}'].maxAudioChannels must be positive if specified.");
            }
        }
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
