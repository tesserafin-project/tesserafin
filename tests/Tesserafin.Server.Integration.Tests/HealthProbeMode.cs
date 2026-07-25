namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// How <see cref="SwitchableDatabaseHealthProbe"/> behaves for the duration of one test.
    /// </summary>
    public enum HealthProbeMode
    {
        /// <summary>Delegate to the production probe and the real SQLite database.</summary>
        Real,

        /// <summary>Report the database as unreachable, as the production probe does when SELECT 1 throws.</summary>
        Fail,

        /// <summary>Never answer, until the caller's timeout cancels the token.</summary>
        Hang
    }
}
