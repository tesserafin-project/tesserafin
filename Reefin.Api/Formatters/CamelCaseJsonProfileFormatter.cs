using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;
using Reefin.Extensions.Json;

namespace Reefin.Api.Formatters;

/// <summary>
/// Camel Case Json Profile Formatter.
/// </summary>
public class CamelCaseJsonProfileFormatter : SystemTextJsonOutputFormatter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CamelCaseJsonProfileFormatter"/> class.
    /// </summary>
    public CamelCaseJsonProfileFormatter() : base(JsonDefaults.CamelCaseOptions)
    {
        SupportedMediaTypes.Clear();
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse(JsonDefaults.CamelCaseMediaType));
    }
}
