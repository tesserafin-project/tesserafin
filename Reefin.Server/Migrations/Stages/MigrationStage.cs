using System.Collections.ObjectModel;

namespace Reefin.Server.Migrations.Stages;

/// <summary>
/// Defines a Stage that can be Invoked and Handled at different times from the code.
/// </summary>
internal class MigrationStage : Collection<CodeMigration>
{
    public MigrationStage(ReefinMigrationStageTypes stage)
    {
        Stage = stage;
    }

    public ReefinMigrationStageTypes Stage { get; }
}
