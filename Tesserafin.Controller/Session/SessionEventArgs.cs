#nullable disable

#pragma warning disable CS1591

using System;

namespace Tesserafin.Controller.Session
{
    public class SessionEventArgs : EventArgs
    {
        public SessionInfo SessionInfo { get; set; }
    }
}
