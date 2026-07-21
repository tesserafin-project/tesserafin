#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

public interface IItemPeopleService
{
    IReadOnlyList<PersonInfo> GetPeople(InternalPeopleQuery query);

    IReadOnlyList<PersonInfo> GetPeople(BaseItem item);

    IReadOnlyList<string> GetPeopleNames(InternalPeopleQuery query);

    IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetPeopleNamesByItems(IReadOnlyList<Guid> itemIds, IReadOnlyList<string> personTypes);
}
