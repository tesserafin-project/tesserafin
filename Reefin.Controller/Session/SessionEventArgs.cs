#nullable disable

#pragma warning disable CS1591

using System;

namespace Reefin.Controller.Session
{
    public class SessionEventArgs : EventArgs
    {
        public SessionInfo SessionInfo { get; set; }
    }
}
