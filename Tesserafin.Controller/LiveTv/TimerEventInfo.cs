#pragma warning disable CS1591

using System;

namespace Tesserafin.Controller.LiveTv
{
    public class TimerEventInfo
    {
        public TimerEventInfo(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Guid? ProgramId { get; set; }
    }
}
