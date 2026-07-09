#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.Persistence;

namespace Reefin.Server.Core.Library;

public class ItemPeopleService : IItemPeopleService
{
    private readonly IPeopleRepository _peopleRepository;

    public ItemPeopleService(IPeopleRepository peopleRepository)
    {
        _peopleRepository = peopleRepository;
    }

    public IReadOnlyList<PersonInfo> GetPeople(InternalPeopleQuery query)
    {
        return _peopleRepository.GetPeople(query).Items;
    }

    public IReadOnlyList<PersonInfo> GetPeople(BaseItem item)
    {
        if (item.SupportsPeople)
        {
            var people = GetPeople(new InternalPeopleQuery
            {
                ItemId = item.Id
            });

            if (people.Count > 0)
            {
                return people;
            }
        }

        return [];
    }

    public IReadOnlyList<string> GetPeopleNames(InternalPeopleQuery query)
    {
        return _peopleRepository.GetPeopleNames(query);
    }

    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetPeopleNamesByItems(IReadOnlyList<Guid> itemIds, IReadOnlyList<string> personTypes)
    {
        return _peopleRepository.GetPeopleNamesByItems(itemIds, personTypes);
    }
}
