using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tesserafin.Extensions;

namespace Tesserafin.Api.CodeQlCanary;

/// <summary>
/// TEMPORARY discriminating canary for the cs/log-forging barrier model (#203). Not merged.
/// </summary>
/// <remarks>
/// Four flows from the same recognised remote source to the same logging sink. Exactly one of
/// them passes through the modelled helper. With the model active the first must produce no
/// cs/log-forging result and the other three must each still be reported; that is what
/// distinguishes a barrier on one exact return value from a barrier on a method name or on a
/// logging category.
/// </remarks>
public static class LogForgingCanary
{
    /// <summary>
    /// Case 1: through the exact modelled helper. Must NOT be reported.
    /// </summary>
    /// <param name="context">The request whose path is the untrusted value.</param>
    /// <param name="logger">The logger the value reaches.</param>
    public static void ThroughTheModelledHelper(HttpContext context, ILogger logger)
    {
        logger.LogInformation("canary {Value}", context.Request.Path.ToString().ToSingleLogLine());
    }

    /// <summary>
    /// Case 2: the same value logged directly. Must remain reported.
    /// </summary>
    /// <param name="context">The request whose path is the untrusted value.</param>
    /// <param name="logger">The logger the value reaches.</param>
    public static void LoggedDirectly(HttpContext context, ILogger logger)
    {
        logger.LogInformation("canary {Value}", context.Request.Path.ToString());
    }

    /// <summary>
    /// Case 3: through an identity helper in this type. Must remain reported.
    /// </summary>
    /// <param name="context">The request whose path is the untrusted value.</param>
    /// <param name="logger">The logger the value reaches.</param>
    public static void ThroughAnIdentityHelper(HttpContext context, ILogger logger)
    {
        logger.LogInformation("canary {Value}", Identity(context.Request.Path.ToString()));
    }

    /// <summary>
    /// Case 4: through a look-alike <c>ToSingleLogLine</c> in another namespace and type, which
    /// returns its input unchanged. Must remain reported. It is referenced by its full name
    /// rather than imported: importing it would make the call in case 1 ambiguous, which is the
    /// same shadowing that makes a name-based canary prove nothing.
    /// </summary>
    /// <param name="context">The request whose path is the untrusted value.</param>
    /// <param name="logger">The logger the value reaches.</param>
    public static void ThroughALookAlike(HttpContext context, ILogger logger)
    {
        logger.LogInformation(
            "canary {Value}",
            CodeQlCanaryLookAlike.LookAlikeLogValueExtensions.ToSingleLogLine(
                context.Request.Path.ToString()));
    }

    private static string? Identity(string? value)
    {
        return value;
    }
}
