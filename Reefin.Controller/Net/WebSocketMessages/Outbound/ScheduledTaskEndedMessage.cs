using System.ComponentModel;
using Reefin.Model.Session;
using Reefin.Model.Tasks;

namespace Reefin.Controller.Net.WebSocketMessages.Outbound;

/// <summary>
/// Scheduled task ended message.
/// </summary>
public class ScheduledTaskEndedMessage : OutboundWebSocketMessage<TaskResult>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduledTaskEndedMessage"/> class.
    /// </summary>
    /// <param name="data">Task result.</param>
    public ScheduledTaskEndedMessage(TaskResult data)
        : base(data)
    {
    }

    /// <inheritdoc />
    [DefaultValue(SessionMessageType.ScheduledTaskEnded)]
    public override SessionMessageType MessageType => SessionMessageType.ScheduledTaskEnded;
}
