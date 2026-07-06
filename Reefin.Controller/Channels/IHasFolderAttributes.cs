#pragma warning disable CA1819, CS1591

namespace Reefin.Controller.Channels
{
    public interface IHasFolderAttributes
    {
        string[] Attributes { get; }
    }
}
