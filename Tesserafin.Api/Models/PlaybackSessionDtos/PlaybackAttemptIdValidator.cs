using System;
using System.Collections.Generic;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// Issue #43: validation for the client-supplied <c>PlaybackAttemptId</c> — the identifier shared by
/// every request of a single playback attempt, retries included.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope, and why it cannot be the request id.</b> One playback attempt emits several
/// independent HTTP requests — <c>PlaybackInfo</c>, <c>POST Playback/Sessions</c>,
/// <c>GET .../Stream</c>, <c>PUT .../{id}</c>, any number of retries, then <c>DELETE</c>. Nothing
/// tied them together before: <c>PlaybackInfo</c> precedes session creation, so
/// <c>PlaySessionId</c>/<c>PlaybackSessionId</c> cannot cover the whole attempt, and a retry that
/// restarts from a fresh <c>POST</c> breaks the correlation again. The <c>RequestId</c>/<c>TraceId</c>
/// of issue #42 cannot solve it either: by construction it changes on every request. Hence a
/// separate, client-generated value that stays put — the reason issue #34 was split in two.
/// </para>
/// <para>
/// <b>Opaque.</b> The server imposes no structure whatsoever: not a GUID, not hex, not a prefix.
/// A client may use whatever it likes. Only two things are checked, and only because an unbounded
/// or unprintable value would be a liability in a log file rather than a correlation aid: a length
/// cap, and the absence of control characters. Nothing is parsed out of the value, and no meaning is
/// ever derived from its content.
/// </para>
/// <para>
/// <b>Never an authorization key.</b> No access decision is derived from it, it grants nothing, and
/// it replaces no existing access control. Two clients presenting the same value gain nothing from
/// each other.
/// </para>
/// <para>
/// <b>Optional.</b> A third-party client that never sends it is completely unaffected: absence is
/// <c>null</c>, and <c>null</c> is valid everywhere. Only a value that IS supplied and IS malformed
/// is rejected.
/// </para>
/// </remarks>
public static class PlaybackAttemptIdValidator
{
    /// <summary>
    /// The maximum accepted length, in characters. Comfortably above any sane client scheme (a GUID
    /// is 36, a GUID pair 73) while keeping the value bounded in every log line and diagnostics
    /// payload it lands in.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>
    /// The structured-log property name the attempt id is published under. One name everywhere, so
    /// a log query for a whole attempt never has to know which endpoint emitted the line. Sits
    /// beside — never replaces — <c>RequestId</c> (issue #42): a single log line normally carries
    /// both, and the pair is what makes the scopes readable at a glance.
    /// </summary>
    public const string LogPropertyName = "PlaybackAttemptId";

    /// <summary>
    /// Validates a supplied attempt id, appending a message to <paramref name="errors"/> for each
    /// violation found.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is valid and produces no error — the field is optional, and a client
    /// that omits it must keep playing normally. An empty or whitespace-only string is NOT treated
    /// as "omitted": a client that sent the field meant to correlate something, and silently
    /// accepting a blank value would produce an attempt bucket that quietly merges every unrelated
    /// attempt that also sent a blank.
    /// </remarks>
    /// <param name="value">The supplied value, or <see langword="null"/> when the client omitted it.</param>
    /// <param name="errors">The error accumulator to append to.</param>
    public static void Validate(string? value, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add("playbackAttemptId must not be empty or whitespace when supplied - omit the field entirely instead.");
            return;
        }

        if (value.Length > MaxLength)
        {
            errors.Add($"playbackAttemptId must be at most {MaxLength} characters when supplied (got {value.Length}).");
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                errors.Add("playbackAttemptId must not contain control characters when supplied.");
                break;
            }
        }
    }

    /// <summary>
    /// Validates a supplied attempt id on its own, throwing <see cref="ArgumentException"/> when it
    /// is present and malformed. <c>Tesserafin.Api.Middleware.ExceptionMiddleware</c> maps that to 400 —
    /// the same idiom <see cref="PlaybackSessionRequestValidator"/> uses.
    /// </summary>
    /// <param name="value">The supplied value, or <see langword="null"/> when the client omitted it.</param>
    /// <exception cref="ArgumentException">The value was supplied and is malformed.</exception>
    public static void ValidateOrThrow(string? value)
    {
        var errors = new List<string>();
        Validate(value, errors);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors));
        }
    }
}
