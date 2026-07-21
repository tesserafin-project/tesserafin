using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.SystemBackupService;
using Tesserafin.Database.Implementations;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Core.Serialization;
using Tesserafin.Server.Implementations.SystemBackupService;
using Tesserafin.Server.Migrations.Stages;
using Tesserafin.Server.ServerSetupApp;

namespace Tesserafin.Server.Migrations;

/// <summary>
/// Handles Migration of the Tesserafin data structure.
/// </summary>
internal class TesserafinMigrationService
{
    private const string DbFilename = "library.db";
    private readonly IDbContextFactory<TesserafinDbContext> _dbContextFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IStartupLogger _startupLogger;
    private readonly IBackupService? _backupService;
    private readonly ITesserafinDatabaseProvider? _reefinDatabaseProvider;
    private readonly IApplicationPaths _applicationPaths;
    private (string? LibraryDb, string? TesserafinDb, BackupManifestDto? FullBackup) _backupKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="TesserafinMigrationService"/> class.
    /// </summary>
    /// <param name="dbContextFactory">Provides access to the reefin database.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="startupLogger">The startup logger for Startup UI intigration.</param>
    /// <param name="applicationPaths">Application paths for library.db backup.</param>
    /// <param name="backupService">The reefin backup service.</param>
    /// <param name="reefinDatabaseProvider">The reefin database provider.</param>
    public TesserafinMigrationService(
        IDbContextFactory<TesserafinDbContext> dbContextFactory,
        ILoggerFactory loggerFactory,
        IStartupLogger<TesserafinMigrationService> startupLogger,
        IApplicationPaths applicationPaths,
        IBackupService? backupService = null,
        ITesserafinDatabaseProvider? reefinDatabaseProvider = null)
    {
        _dbContextFactory = dbContextFactory;
        _loggerFactory = loggerFactory;
        _startupLogger = startupLogger;
        _backupService = backupService;
        _reefinDatabaseProvider = reefinDatabaseProvider;
        _applicationPaths = applicationPaths;
#pragma warning disable CS0618 // Type or member is obsolete
        Migrations = [.. typeof(IMigrationRoutine).Assembly.GetTypes().Where(e => typeof(IMigrationRoutine).IsAssignableFrom(e) || typeof(IAsyncMigrationRoutine).IsAssignableFrom(e))
            .Select(e => (Type: e, Metadata: e.GetCustomAttribute<TesserafinMigrationAttribute>(), Backup: e.GetCustomAttributes<TesserafinMigrationBackupAttribute>()))
            .Where(e => e.Metadata is not null)
            .GroupBy(e => e.Metadata!.Stage)
            .Select(f =>
            {
                var stage = new MigrationStage(f.Key);
                foreach (var item in f)
                {
                    TesserafinMigrationBackupAttribute? backupMetadata = null;
                    if (item.Backup?.Any() == true)
                    {
                        backupMetadata = item.Backup.Aggregate(MergeBackupAttributes);
                    }

                    stage.Add(new(item.Type, item.Metadata!, backupMetadata));
                }

                return stage;
            })];
#pragma warning restore CS0618 // Type or member is obsolete
    }

    private interface IInternalMigration
    {
        Task PerformAsync(IStartupLogger logger);
    }

    private HashSet<MigrationStage> Migrations { get; set; }

    public async Task CheckFirstTimeRunOrMigration(IApplicationPaths appPaths, StartupOptions startupOptions)
    {
        var logger = _startupLogger.With(_loggerFactory.CreateLogger<TesserafinMigrationService>()).BeginGroup($"Migration Startup");
        logger.LogInformation("Initialise Migration service.");
        var xmlSerializer = new MyXmlSerializer();
        var serverConfig = File.Exists(appPaths.SystemConfigurationFilePath)
            ? (ServerConfiguration)xmlSerializer.DeserializeFromFile(typeof(ServerConfiguration), appPaths.SystemConfigurationFilePath)!
            : new ServerConfiguration();
        if (!serverConfig.IsStartupWizardCompleted || startupOptions.StartupMode is Configuration.StartupMode.SeedSystem)
        {
            logger.LogInformation("System initialization detected. Seed data. Startup mode is: {StartupMode}", startupOptions.StartupMode ?? Configuration.StartupMode.MediaServer);
            var flatApplyMigrations = Migrations.SelectMany(e => e.Where(f => !f.Metadata.RunMigrationOnSetup)).ToArray();

            var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                var databaseCreator = dbContext.Database.GetService<IDatabaseCreator>() as IRelationalDatabaseCreator
                    ?? throw new InvalidOperationException("Tesserafin does only support relational databases.");
                if (!await databaseCreator.ExistsAsync().ConfigureAwait(false))
                {
                    await databaseCreator.CreateAsync().ConfigureAwait(false);
                }

                var historyRepository = dbContext.GetService<IHistoryRepository>();

                await historyRepository.CreateIfNotExistsAsync().ConfigureAwait(false);
                var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync().ConfigureAwait(false);
                var startupScripts = flatApplyMigrations
                    .Where(e => !appliedMigrations.Any(f => f != e.BuildCodeMigrationId()))
                    .Select(e => (Migration: e.Metadata, Script: historyRepository.GetInsertScript(new HistoryRow(e.BuildCodeMigrationId(), GetTesserafinVersion()))))
                    .ToArray();
                foreach (var item in startupScripts)
                {
                    logger.LogInformation("Seed migration {Key}-{Name}.", item.Migration.Key, item.Migration.Name);
                    await dbContext.Database.ExecuteSqlRawAsync(item.Script).ConfigureAwait(false);
                }
            }

            logger.LogInformation("Migration system initialisation completed.");
        }
        else
        {
            // migrate any existing migration.xml files
            var migrationConfigPath = Path.Join(appPaths.ConfigurationDirectoryPath, "migrations.xml");
            var migrationOptions = File.Exists(migrationConfigPath)
                 ? (MigrationOptions)xmlSerializer.DeserializeFromFile(typeof(MigrationOptions), migrationConfigPath)!
                 : null;
            if (migrationOptions is not null && migrationOptions.Applied.Count > 0)
            {
                logger.LogInformation("Old migration style migration.xml detected. Migrate now.");
                try
                {
                    var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
                    await using (dbContext.ConfigureAwait(false))
                    {
                        var historyRepository = dbContext.GetService<IHistoryRepository>();
                        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync().ConfigureAwait(false);
                        var lastOldAppliedMigration = Migrations
                            .SelectMany(e => e.Where(e => e.Metadata.Key is not null)) // only consider migrations that have the key set as its the reference marker for legacy migrations.
                            .Where(e => migrationOptions.Applied.Any(f => f.Id.Equals(e.Metadata.Key!.Value)))
                            .Where(e => !appliedMigrations.Contains(e.BuildCodeMigrationId()))
                            .OrderBy(e => e.BuildCodeMigrationId())
                            .Last(); // this is the latest migration applied in the old migration.xml

                        IReadOnlyList<CodeMigration> oldMigrations = [
                            .. Migrations
                            .SelectMany(e => e)
                            .OrderBy(e => e.BuildCodeMigrationId())
                            .TakeWhile(e => e.BuildCodeMigrationId() != lastOldAppliedMigration.BuildCodeMigrationId()),
                            lastOldAppliedMigration
                        ];
                        // those are all migrations that had to run in the old migration system, even if not noted in the migration.xml file.

                        var startupScripts = oldMigrations.Select(e => (Migration: e.Metadata, Script: historyRepository.GetInsertScript(new HistoryRow(e.BuildCodeMigrationId(), GetTesserafinVersion()))));
                        foreach (var item in startupScripts)
                        {
                            logger.LogInformation("Migrate migration {Key}-{Name}.", item.Migration.Key, item.Migration.Name);
                            await dbContext.Database.ExecuteSqlRawAsync(item.Script).ConfigureAwait(false);
                        }

                        logger.LogInformation("Rename old migration.xml to migration.xml.backup");
                        File.Move(migrationConfigPath, Path.ChangeExtension(migrationConfigPath, ".xml.backup"), true);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "Failed to apply migrations");
                    throw;
                }
            }
        }
    }

    public async Task MigrateStepAsync(TesserafinMigrationStageTypes stage, IServiceProvider? serviceProvider)
    {
        var logger = _startupLogger.With(_loggerFactory.CreateLogger<TesserafinMigrationService>()).BeginGroup($"Migrate stage {stage}.");
        ICollection<CodeMigration> migrationStage = (Migrations.FirstOrDefault(e => e.Stage == stage) as ICollection<CodeMigration>) ?? [];

        var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var historyRepository = dbContext.GetService<IHistoryRepository>();
            var migrationsAssembly = dbContext.GetService<IMigrationsAssembly>();
            (string Key, IInternalMigration Migration)[] migrations = [];

            do
            { // migrations may alter the migration state. Reevaluate the applicable migrations after every stage ran until there are no more to apply.
                var appliedMigrations = await historyRepository.GetAppliedMigrationsAsync().ConfigureAwait(false);
                var pendingCodeMigrations = migrationStage
                    .Where(e => appliedMigrations.All(f => f.MigrationId != e.BuildCodeMigrationId()))
                    .Select(e => (Key: e.BuildCodeMigrationId(), Migration: new InternalCodeMigration(e, serviceProvider, dbContext)))
                    .ToArray();

                (string Key, InternalDatabaseMigration Migration)[] pendingDatabaseMigrations = [];
                if (stage is TesserafinMigrationStageTypes.CoreInitialisation)
                {
                    pendingDatabaseMigrations = migrationsAssembly.Migrations.Where(f => appliedMigrations.All(e => e.MigrationId != f.Key))
                       .Select(e => (Key: e.Key, Migration: new InternalDatabaseMigration(e, dbContext)))
                       .ToArray();
                }

                (string Key, IInternalMigration Migration)[] pendingMigrations = [.. pendingCodeMigrations, .. pendingDatabaseMigrations];
                logger.LogInformation("There are {Pending} migrations for stage {Stage}.", pendingCodeMigrations.Length, stage);
                migrations = pendingMigrations.OrderBy(e => e.Key).ToArray();

                var migrationIndex = 0;
                foreach (var item in migrations)
                {
                    // Surface generic "Running migration X of Y" progress in the always-visible startup UI header.
                    SetupServer.ReportActivity(StartupActivity.Migration(++migrationIndex, migrations.Length));
                    var migrationLogger = logger.With(_loggerFactory.CreateLogger(item.Migration.GetType().Name)).BeginGroup($"{item.Key}");
                    try
                    {
                        migrationLogger.LogInformation("Perform migration {Name}", item.Key);
                        await item.Migration.PerformAsync(migrationLogger).ConfigureAwait(false);
                        migrationLogger.LogInformation("Migration {Name} was successfully applied", item.Key);
                    }
                    catch (Exception ex)
                    {
                        migrationLogger.LogCritical("Error: {Error}", ex.Message);
                        migrationLogger.LogError(ex, "Migration {Name} failed", item.Key);

                        if (_backupKey != default && _backupService is not null && _reefinDatabaseProvider is not null)
                        {
                            if (_backupKey.LibraryDb is not null)
                            {
                                migrationLogger.LogInformation("Attempt to rollback librarydb.");
                                try
                                {
                                    var libraryDbPath = Path.Combine(_applicationPaths.DataPath, DbFilename);
                                    File.Move(_backupKey.LibraryDb, libraryDbPath, true);
                                }
                                catch (Exception inner)
                                {
                                    migrationLogger.LogCritical(inner, "Could not rollback {LibraryPath}. Manual intervention might be required to restore a operational state.", _backupKey.LibraryDb);
                                }
                            }

                            if (_backupKey.TesserafinDb is not null)
                            {
                                migrationLogger.LogInformation("Attempt to rollback TesserafinDb.");
                                try
                                {
                                    await _reefinDatabaseProvider.RestoreBackupFast(_backupKey.TesserafinDb, CancellationToken.None).ConfigureAwait(false);
                                }
                                catch (Exception inner)
                                {
                                    migrationLogger.LogCritical(inner, "Could not rollback {LibraryPath}. Manual intervention might be required to restore a operational state.", _backupKey.TesserafinDb);
                                }
                            }

                            if (_backupKey.FullBackup is not null)
                            {
                                migrationLogger.LogInformation("Attempt to rollback from backup.");
                                try
                                {
                                    await _backupService.RestoreBackupAsync(_backupKey.FullBackup.Path).ConfigureAwait(false);
                                }
                                catch (Exception inner)
                                {
                                    migrationLogger.LogCritical(inner, "Could not rollback from backup {Backup}. Manual intervention might be required to restore a operational state.", _backupKey.FullBackup.Path);
                                }
                            }
                        }

                        throw;
                    }
                }
            } while (migrations.Length != 0);
        }
    }

    private static string GetTesserafinVersion()
    {
        return Assembly.GetEntryAssembly()!.GetName().Version!.ToString();
    }

    public async Task CleanupSystemAfterMigration(ILogger logger)
    {
        if (_backupKey != default)
        {
            if (_backupKey.LibraryDb is not null)
            {
                logger.LogInformation("Attempt to cleanup librarydb backup.");
                try
                {
                    File.Delete(_backupKey.LibraryDb);
                }
                catch (Exception inner)
                {
                    logger.LogCritical(inner, "Could not cleanup {LibraryPath}.", _backupKey.LibraryDb);
                }
            }

            if (_backupKey.TesserafinDb is not null && _reefinDatabaseProvider is not null)
            {
                logger.LogInformation("Attempt to cleanup TesserafinDb backup.");
                try
                {
                    await _reefinDatabaseProvider.DeleteBackup(_backupKey.TesserafinDb).ConfigureAwait(false);
                }
                catch (Exception inner)
                {
                    logger.LogCritical(inner, "Could not cleanup {LibraryPath}.", _backupKey.TesserafinDb);
                }
            }

            if (_backupKey.FullBackup is not null)
            {
                logger.LogInformation("Attempt to cleanup from migration backup.");
                try
                {
                    File.Delete(_backupKey.FullBackup.Path);
                }
                catch (Exception inner)
                {
                    logger.LogCritical(inner, "Could not cleanup backup {Backup}.", _backupKey.FullBackup.Path);
                }
            }
        }
    }

    public async Task PrepareSystemForMigration(ILogger logger)
    {
        logger.LogInformation("Prepare system for possible migrations");
        TesserafinMigrationBackupAttribute backupInstruction;
        IReadOnlyList<HistoryRow> appliedMigrations;
        var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var historyRepository = dbContext.GetService<IHistoryRepository>();
            var migrationsAssembly = dbContext.GetService<IMigrationsAssembly>();
            appliedMigrations = await historyRepository.GetAppliedMigrationsAsync().ConfigureAwait(false);
            backupInstruction = new TesserafinMigrationBackupAttribute()
            {
                TesserafinDb = migrationsAssembly.Migrations.Any(f => appliedMigrations.All(e => e.MigrationId != f.Key))
            };
        }

        backupInstruction = Migrations.SelectMany(e => e)
           .Where(e => appliedMigrations.All(f => f.MigrationId != e.BuildCodeMigrationId()))
           .Select(e => e.BackupRequirements)
           .Where(e => e is not null)
           .Aggregate(backupInstruction, MergeBackupAttributes!);

        if (backupInstruction.LegacyLibraryDb)
        {
            logger.LogInformation("A migration will attempt to modify the library.db, will attempt to backup the file now.");
            // for legacy migrations that still operates on the library.db
            var libraryDbPath = Path.Combine(_applicationPaths.DataPath, DbFilename);
            if (File.Exists(libraryDbPath))
            {
                for (int i = 1; ; i++)
                {
                    var bakPath = string.Format(CultureInfo.InvariantCulture, "{0}.bak{1}", libraryDbPath, i);
                    if (!File.Exists(bakPath))
                    {
                        try
                        {
                            logger.LogInformation("Backing up {Library} to {BackupPath}", DbFilename, bakPath);
                            File.Copy(libraryDbPath, bakPath);
                            _backupKey = (bakPath, _backupKey.TesserafinDb, _backupKey.FullBackup);
                            logger.LogInformation("{Library} backed up to {BackupPath}", DbFilename, bakPath);
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Cannot make a backup of {Library} at path {BackupPath}", DbFilename, bakPath);
                            throw;
                        }
                    }
                }

                logger.LogInformation("{Library} has been backed up as {BackupPath}", DbFilename, _backupKey.LibraryDb);
            }
            else
            {
                logger.LogError("Cannot make a backup of {Library} at path {BackupPath} because file could not be found at {LibraryPath}", DbFilename, libraryDbPath, _applicationPaths.DataPath);
            }
        }

        if (backupInstruction.TesserafinDb && _reefinDatabaseProvider is not null)
        {
            logger.LogInformation("A migration will attempt to modify the tesserafin.db, will attempt to backup the file now.");
            _backupKey = (_backupKey.LibraryDb, await _reefinDatabaseProvider.MigrationBackupFast(CancellationToken.None).ConfigureAwait(false), _backupKey.FullBackup);
            logger.LogInformation("Tesserafin database has been backed up as {BackupPath}", _backupKey.TesserafinDb);
        }

        if (_backupService is not null && (backupInstruction.Metadata || backupInstruction.Subtitles || backupInstruction.Trickplay))
        {
            logger.LogInformation("A migration will attempt to modify system resources. Will attempt to create backup now.");
            _backupKey = (_backupKey.LibraryDb, _backupKey.TesserafinDb, await _backupService.CreateBackupAsync(new BackupOptionsDto()
            {
                Metadata = backupInstruction.Metadata,
                Subtitles = backupInstruction.Subtitles,
                Trickplay = backupInstruction.Trickplay,
                Database = false // database backups are explicitly handled by the provider itself as the backup service requires parity with the current model
            }).ConfigureAwait(false));
            logger.LogInformation("Pre-Migration backup successfully created as {BackupKey}", _backupKey.FullBackup.Path);
        }
    }

    private static TesserafinMigrationBackupAttribute MergeBackupAttributes(TesserafinMigrationBackupAttribute left, TesserafinMigrationBackupAttribute right)
    {
        return new TesserafinMigrationBackupAttribute()
        {
            TesserafinDb = left!.TesserafinDb || right!.TesserafinDb,
            LegacyLibraryDb = left.LegacyLibraryDb || right!.LegacyLibraryDb,
            Metadata = left.Metadata || right!.Metadata,
            Subtitles = left.Subtitles || right!.Subtitles,
            Trickplay = left.Trickplay || right!.Trickplay
        };
    }

    private class InternalCodeMigration : IInternalMigration
    {
        private readonly CodeMigration _codeMigration;
        private readonly IServiceProvider? _serviceProvider;
        private TesserafinDbContext _dbContext;

        public InternalCodeMigration(CodeMigration codeMigration, IServiceProvider? serviceProvider, TesserafinDbContext dbContext)
        {
            _codeMigration = codeMigration;
            _serviceProvider = serviceProvider;
            _dbContext = dbContext;
        }

        public async Task PerformAsync(IStartupLogger logger)
        {
            await _codeMigration.Perform(_serviceProvider, logger, CancellationToken.None).ConfigureAwait(false);

            var historyRepository = _dbContext.GetService<IHistoryRepository>();
            var createScript = historyRepository.GetInsertScript(new HistoryRow(_codeMigration.BuildCodeMigrationId(), GetTesserafinVersion()));
            await _dbContext.Database.ExecuteSqlRawAsync(createScript).ConfigureAwait(false);
        }
    }

    private class InternalDatabaseMigration : IInternalMigration
    {
        private readonly TesserafinDbContext _reefinDbContext;
        private KeyValuePair<string, TypeInfo> _databaseMigrationInfo;

        public InternalDatabaseMigration(KeyValuePair<string, TypeInfo> databaseMigrationInfo, TesserafinDbContext reefinDbContext)
        {
            _databaseMigrationInfo = databaseMigrationInfo;
            _reefinDbContext = reefinDbContext;
        }

        public async Task PerformAsync(IStartupLogger logger)
        {
            var migrator = _reefinDbContext.GetService<IMigrator>();
            await migrator.MigrateAsync(_databaseMigrationInfo.Key).ConfigureAwait(false);
        }
    }
}
