#pragma warning disable CS1591

namespace Reefin.Model.LiveTv
{
    public enum RecordingStatus
    {
        New,
        InProgress,
        Completed,
        Cancelled,
        ConflictedOk,
        ConflictedNotOk,
        Error
    }
}
