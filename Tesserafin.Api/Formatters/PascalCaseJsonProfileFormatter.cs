using System.Net.Mime;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;
using Tesserafin.Extensions.Json;

namespace Tesserafin.Api.Formatters;

/// <summary>
/// Pascal Case Json Profile Formatter.
/// </summary>
public class PascalCaseJsonProfileFormatter : SystemTextJsonOutputFormatter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PascalCaseJsonProfileFormatter"/> class.
    /// </summary>
    public PascalCaseJsonProfileFormatter() : base(JsonDefaults.PascalCaseOptions)
    {
        SupportedMediaTypes.Clear();
        // Add application/json for default formatter
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json));
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse(JsonDefaults.PascalCaseMediaType));
    }
}
