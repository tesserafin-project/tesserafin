using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reefin.Database.Implementations;
using Reefin.Model.Globalization;
using Reefin.Server.ServerSetupApp;

namespace Reefin.Server.Migrations.Routines;

/// <summary>
/// Migrate rating levels.
/// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
[ReefinMigration("2026-03-02T09:00:00", nameof(MigrateRatingLevels))]
[ReefinMigrationBackup(ReefinDb = true)]
#pragma warning restore CS0618 // Type or member is obsolete
internal class MigrateRatingLevels : IDatabaseMigrationRoutine
{
    private readonly IStartupLogger _logger;
    private readonly IDbContextFactory<ReefinDbContext> _provider;
    private readonly ILocalizationManager _localizationManager;

    public MigrateRatingLevels(
        IDbContextFactory<ReefinDbContext> provider,
        IStartupLogger<MigrateRatingLevels> logger,
        ILocalizationManager localizationManager)
    {
        _provider = provider;
        _localizationManager = localizationManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Perform()
    {
        _logger.LogInformation("Recalculating parental rating levels based on rating string.");
        using var context = _provider.CreateDbContext();
        using var transaction = context.Database.BeginTransaction();
        var ratings = context.BaseItems.AsNoTracking().Select(e => e.OfficialRating).Distinct();
        foreach (var rating in ratings)
        {
            if (string.IsNullOrEmpty(rating))
            {
                int? value = null;
                context.BaseItems
                    .Where(e => e.OfficialRating == null || e.OfficialRating == string.Empty)
                    .ExecuteUpdate(f => f.SetProperty(e => e.InheritedParentalRatingValue, value));
                context.BaseItems
                    .Where(e => e.OfficialRating == null || e.OfficialRating == string.Empty)
                    .ExecuteUpdate(f => f.SetProperty(e => e.InheritedParentalRatingSubValue, value));
            }
            else
            {
                var ratingValue = _localizationManager.GetRatingScore(rating);
                var score = ratingValue?.Score;
                var subScore = ratingValue?.SubScore;
                context.BaseItems
                    .Where(e => e.OfficialRating == rating)
                    .ExecuteUpdate(f => f.SetProperty(e => e.InheritedParentalRatingValue, score));
                context.BaseItems
                    .Where(e => e.OfficialRating == rating)
                    .ExecuteUpdate(f => f.SetProperty(e => e.InheritedParentalRatingSubValue, subScore));
            }
        }

        transaction.Commit();
    }
}
