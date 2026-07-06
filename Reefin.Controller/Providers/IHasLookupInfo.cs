#pragma warning disable CS1591

namespace Reefin.Controller.Providers
{
    public interface IHasLookupInfo<out TLookupInfoType>
        where TLookupInfoType : ItemLookupInfo, new()
    {
        TLookupInfoType GetLookupInfo();
    }
}
