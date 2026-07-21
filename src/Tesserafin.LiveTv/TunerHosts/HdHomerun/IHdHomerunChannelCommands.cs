#pragma warning disable CS1591

using System.Collections.Generic;

namespace Tesserafin.LiveTv.TunerHosts.HdHomerun
{
    public interface IHdHomerunChannelCommands
    {
        IEnumerable<(string CommandName, string CommandValue)> GetCommands();
    }
}
