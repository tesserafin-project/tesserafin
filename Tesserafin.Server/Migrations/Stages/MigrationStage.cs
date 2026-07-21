using System.Collections.ObjectModel;

namespace Tesserafin.Server.Migrations.Stages;

/// <summary>
/// Defines a Stage that can be Invoked and Handled at different times from the code.
/// </summary>
internal class MigrationStage : Collection<CodeMigration>
{
    public MigrationStage(TesserafinMigrationStageTypes stage)
    {
        Stage = stage;
    }

    public TesserafinMigrationStageTypes Stage { get; }
}
