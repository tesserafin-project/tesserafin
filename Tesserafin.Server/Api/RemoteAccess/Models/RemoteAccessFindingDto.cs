namespace Tesserafin.Server.Api.RemoteAccess.Models;

/// <summary>
/// One thing the server can say, and how strongly it can say it.
/// </summary>
public sealed class RemoteAccessFindingDto
{
    /// <summary>Gets or sets what was found.</summary>
    public RemoteAccessFindingCode Code { get; set; }

    /// <summary>Gets or sets how the server knows it.</summary>
    public RemoteAccessFindingConfidence Confidence { get; set; }

    /// <summary>Gets or sets how much attention it deserves.</summary>
    public RemoteAccessFindingSeverity Severity { get; set; }
}
