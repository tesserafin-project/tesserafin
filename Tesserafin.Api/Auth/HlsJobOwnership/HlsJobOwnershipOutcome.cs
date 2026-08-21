namespace Tesserafin.Api.Auth.HlsJobOwnership;

/// <summary>
/// The three answers the ownership authorizer can give (#153-LTV-R3).
/// </summary>
public enum HlsJobOwnershipOutcome
{
    /// <summary>
    /// No active job owns what the caller named. The resource is unreachable, whoever asks: a
    /// job's output files outlive it, and once the job is gone they are not served again.
    /// </summary>
    NoSuchJob = 0,

    /// <summary>
    /// A job owns it and this caller is not entitled to it.
    /// </summary>
    Refused = 1,

    /// <summary>
    /// The caller is the job's owner, or presented a capability that matches the job exactly.
    /// </summary>
    Authorized = 2
}
