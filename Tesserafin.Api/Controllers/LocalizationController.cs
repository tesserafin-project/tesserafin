using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Auth.FirstTimeSetupPolicy;
using Tesserafin.Common.Api;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// Localization controller.
/// </summary>
[Authorize(Policy = Policies.FirstTimeSetupOrDefault)]
[FirstTimeSetupEndpoint]
public class LocalizationController : BaseTesserafinApiController
{
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalizationController"/> class.
    /// </summary>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    public LocalizationController(ILocalizationManager localization)
    {
        _localization = localization;
    }

    /// <summary>
    /// Gets known cultures.
    /// </summary>
    /// <response code="200">Known cultures returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the list of cultures.</returns>
    [HttpGet("Cultures")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<CultureDto>> GetCultures()
    {
        var allCultures = _localization.GetCultures();

        var distinctCultures = allCultures
            .DistinctBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.DisplayName)
            .AsEnumerable();

        return Ok(distinctCultures);
    }

    /// <summary>
    /// Gets known countries.
    /// </summary>
    /// <response code="200">Known countries returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the list of countries.</returns>
    [HttpGet("Countries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CountryInfo>> GetCountries()
    {
        return Ok(_localization.GetCountries());
    }

    /// <summary>
    /// Gets known parental ratings.
    /// </summary>
    /// <response code="200">Known parental ratings returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the list of parental ratings.</returns>
    [HttpGet("ParentalRatings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ParentalRating>> GetParentalRatings()
    {
        return Ok(_localization.GetParentalRatings());
    }

    /// <summary>
    /// Gets localization options.
    /// </summary>
    /// <response code="200">Localization options returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the list of localization options.</returns>
    [HttpGet("Options")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<LocalizationOption>> GetLocalizationOptions()
    {
        return Ok(_localization.GetLocalizationOptions());
    }
}
