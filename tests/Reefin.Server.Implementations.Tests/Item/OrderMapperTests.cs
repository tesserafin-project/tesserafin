using System;
using Reefin.Controller.Entities;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Entities;
using Reefin.Server.Implementations.Item;
using Xunit;

namespace Reefin.Server.Implementations.Tests.Item;

public class OrderMapperTests
{
    [Fact]
    public void ShouldReturnMappedOrderForSortingByPremierDate()
    {
        var orderFunc = OrderMapper.MapOrderByField(ItemSortBy.PremiereDate, new InternalItemsQuery(), null!).Compile();

        var expectedDate = new DateTime(1, 2, 3);
        var expectedProductionYearDate = new DateTime(4, 1, 1);

        var entityWithOnlyProductionYear = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test", ProductionYear = expectedProductionYearDate.Year };
        var entityWithOnlyPremierDate = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test", PremiereDate = expectedDate };
        var entityWithBothPremierDateAndProductionYear = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test", PremiereDate = expectedDate, ProductionYear = expectedProductionYearDate.Year };
        var entityWithoutEitherPremierDateOrProductionYear = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test" };

        var resultWithOnlyProductionYear = orderFunc(entityWithOnlyProductionYear);
        var resultWithOnlyPremierDate = orderFunc(entityWithOnlyPremierDate);
        var resultWithBothPremierDateAndProductionYear = orderFunc(entityWithBothPremierDateAndProductionYear);
        var resultWithoutEitherPremierDateOrProductionYear = orderFunc(entityWithoutEitherPremierDateOrProductionYear);

        Assert.Equal(resultWithOnlyProductionYear, expectedProductionYearDate);
        Assert.Equal(resultWithOnlyPremierDate, expectedDate);
        Assert.Equal(resultWithBothPremierDateAndProductionYear, expectedDate);
        Assert.Null(resultWithoutEitherPremierDateOrProductionYear);
    }
}
