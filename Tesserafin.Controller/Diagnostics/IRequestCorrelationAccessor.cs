namespace Tesserafin.Controller.Diagnostics;

/// <summary>
/// Issue #42: ambient access to the correlation identifier of the HTTP request currently being
/// served on this execution context, for code that must log it but sits below (and must not depend
/// on) the ASP.NET Core hosting layer — <c>Tesserafin.MediaEncoding</c> in particular.
/// </summary>
/// <remarks>
/// <para>
/// Scope discipline — the whole point of this abstraction, and the reason issue #34 was split into
/// #42 and #43. There are three nested scopes, and no single identifier can serve two of them:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>One HTTP request</b> — the value returned here. Derived from <c>Activity.TraceId</c> /
/// <c>HttpContext.TraceIdentifier</c>, server-generated, and <b>different for every request</b>,
/// including two requests belonging to the same playback attempt or the same session.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>One playback attempt</b> — <c>PlaybackAttemptId</c> (issue #43). Client-supplied, opaque, and
/// <b>stable across several requests</b>, retries included. Explicitly NOT this identifier.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>One server session</b> — <c>PlaySessionId</c> / <c>PlaybackSessionId</c>. Lives for the whole
/// transcoding/playback session, potentially hours, spanning many attempts.
/// </description>
/// </item>
/// </list>
/// <para>
/// This identifier is diagnostics only. It is never an authorization key, it grants no access, and
/// it replaces no existing identifier: it is purely additive to the log record.
/// </para>
/// </remarks>
public interface IRequestCorrelationAccessor
{
    /// <summary>
    /// Gets the correlation identifier of the request currently in flight on this execution
    /// context, or <c>null</c> when there is none — a background timer, a scheduled task, a startup
    /// path, or any non-HTTP caller. Callers must treat <c>null</c> as "not correlatable" and carry
    /// on: no diagnostics path may ever fail playback.
    /// </summary>
    string? CurrentRequestId { get; }
}
