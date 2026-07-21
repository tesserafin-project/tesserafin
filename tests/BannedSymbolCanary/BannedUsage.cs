using System.Text.Json;

namespace BannedSymbolCanary;

// Issue #75 slice 75b: this file deliberately reaches for a banned symbol. Building this project MUST
// fail with RS0030 - that failure is the assertion PlaybackContractScannerBanTests makes. If this
// ever compiles, the scan-path ban has stopped working.
internal static class BannedUsage
{
    public static string Materialize(ref Utf8JsonReader reader)
    {
        // BANNED on the scan path: materializing a client value out of the reader.
        return reader.GetString() ?? string.Empty;
    }
}
