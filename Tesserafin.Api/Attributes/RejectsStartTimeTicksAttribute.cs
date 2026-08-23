using System;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Tesserafin.Api.Attributes;

/// <summary>
/// Refuses a caller-supplied <c>startTimeTicks</c> on a route that cannot honour one, at the MVC
/// boundary, before the action body runs.
/// </summary>
/// <remarks>
/// WHY THIS IS A FILTER AND NOT AN <c>if</c> IN THE ACTION. The HLS segment routes serve segments
/// of a transcode that was already positioned when it started; a start offset named per segment is
/// not something they can honour, so it has to be refused. Written as a guard inside
/// <c>GetDynamicSegment</c>, that refusal was a user-controlled branch standing in front of
/// <c>IHlsJobOwnership.AuthorizeByOutputPath</c> — a query parameter deciding whether an
/// authorization call happens at all. That is what CodeQL's <c>cs/user-controlled-bypass</c>
/// reports, and it is a fair reading of the shape even though the branch threw: the ownership
/// decision is no longer unconditional in the method that takes it.
///
/// Moving the refusal here makes it unconditional again. The action body has no branch on this
/// value, the authorizer is the first decision the action takes, and the value is refused before
/// any streaming state is resolved, any output path is named, any file is opened and any
/// transcoding job is attached, created or killed.
///
/// WHY IT IS NOT A DATA ANNOTATION. <c>[Range]</c> would read the same at the call site but the
/// check would run inside model binding, with no <see cref="Microsoft.AspNetCore.Http.HttpContext"/>
/// in scope. The refusal and the ownership decision that follows it have to be observable as
/// decisions taken about the SAME request; a filter is the boundary where that is true.
///
/// WHY IT FAILS CLOSED ON A RENAME. The parameter is named by string, which is how MVC itself
/// addresses bound arguments. A rename that left this attribute behind would otherwise make it
/// silently inert — a filter that examines a parameter that no longer exists refuses nothing. It
/// throws instead, and <c>DynamicHlsStartTimeTicksBoundaryTests</c> asserts the declared name
/// resolves on every decorated action, so the rename is caught before a request ever arrives.
///
/// WHAT IS ALLOWED. Absent, or zero. Nothing else — a negative value is a caller-supplied start
/// offset too, and this route can no more honour a negative one than a positive one.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RejectsStartTimeTicksAttribute : Attribute, IActionFilter
{
    private readonly string _parameterName;

    /// <summary>
    /// Initializes a new instance of the <see cref="RejectsStartTimeTicksAttribute"/> class.
    /// </summary>
    /// <param name="parameterName">The name of the action parameter carrying the start offset.</param>
    public RejectsStartTimeTicksAttribute(string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterName);
        _parameterName = parameterName;
    }

    /// <summary>
    /// Gets the name of the action parameter this filter refuses a non-zero value on. Public so
    /// the boundary can be asserted against the route table without issuing a request.
    /// </summary>
    public string ParameterName => _parameterName;

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.ActionDescriptor.Parameters.Any(
                p => string.Equals(p.Name, _parameterName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is applied to an action that declares no parameter named '{1}'.",
                nameof(RejectsStartTimeTicksAttribute),
                _parameterName));
        }

        // A missing query key leaves the argument unbound and out of this dictionary, which is the
        // "absent" case and is allowed. An unparseable one never reaches here: model binding fails
        // and [ApiController]'s model-state filter answers 400 ahead of every action filter.
        if (context.ActionArguments.TryGetValue(_parameterName, out var bound)
            && bound is long ticks
            && ticks != 0)
        {
            // STATUS ONLY, AND NOT BadRequestResult. [ApiController] maps every
            // IClientErrorActionResult — BadRequestResult and StatusCodeResult among them —
            // through ClientErrorResultFilter into a ProblemDetails document, so returning one
            // here would answer a media segment request with a JSON body. MEASURED: the first
            // version of this filter did exactly that, and the HTTP matrix caught it. ContentResult
            // is not an IClientErrorActionResult, so this is 400 and nothing else: no document to
            // parse, no echo of the offending value, and the same answer whoever asked.
            context.Result = new ContentResult { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
        // Nothing to do once the action has run: this filter only ever prevents it from running.
    }
}
