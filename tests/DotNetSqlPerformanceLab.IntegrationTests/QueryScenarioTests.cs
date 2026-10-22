using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNetSqlPerformanceLab.Infrastructure.Queries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DotNetSqlPerformanceLab.IntegrationTests;

[CollectionDefinition("PostgreSQL query integration tests", DisableParallelization = true)]
public sealed class PostgreSqlQueryCollection
{
    public const string Name = "PostgreSQL query integration tests";
}

[Collection(PostgreSqlQueryCollection.Name)]
public sealed class QueryScenarioTests
{
    [Fact]
    public async Task CustomerHistory_TrackedAndProjectionReturnTheSameOrderedFields()
    {
        var data = await PostgresTestSupport.CreateDataAsync();
        try
        {
            CustomerHistoryResponse? tracked;
            await using (var context = PostgresTestSupport.CreateContext())
            {
                tracked = await new OrderQueryService(context).GetCustomerHistoryTrackedAsync(data.FirstCustomerId);
                Assert.NotEmpty(context.ChangeTracker.Entries());
            }

            CustomerHistoryResponse? projection;
            await using (var context = PostgresTestSupport.CreateContext())
            {
                projection = await new OrderQueryService(context).GetCustomerHistoryProjectionAsync(data.FirstCustomerId);
                Assert.Empty(context.ChangeTracker.Entries());
            }

            Assert.Equal(JsonSerializer.Serialize(tracked), JsonSerializer.Serialize(projection));
            Assert.Equal(new[]
                {
                    data.FirstCustomerOrderIds[1], data.FirstCustomerOrderIds[0], data.FirstCustomerOrderIds[2]
                },
                tracked!.Orders.Select(order => order.OrderId));
            Assert.Null(await new OrderQueryService(PostgresTestSupport.CreateContext())
                .GetCustomerHistoryProjectionAsync(int.MaxValue));
        }
        finally { await PostgresTestSupport.CleanupAsync(data); }
    }

    [Fact]
    public async Task OrderSearch_BaselineAndTranslatedQueriesMatchFiltersAndStableOrder()
    {
        var data = await PostgresTestSupport.CreateDataAsync();
        try
        {
            var request = new OrderSearchRequest(
                "Paid",
                new DateTimeOffset(2001, 2, 2, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2001, 2, 4, 10, 0, 0, TimeSpan.Zero));
            await using var baselineContext = PostgresTestSupport.CreateContext();
            await using var translatedContext = PostgresTestSupport.CreateContext();
            var baseline = await new OrderQueryService(baselineContext).SearchOrdersBaselineAsync(request);
            var translated = await new OrderQueryService(translatedContext).SearchOrdersTranslatedAsync(request);

            Assert.Equal(baseline, translated);
            Assert.Equal(2, translated.Count);
            Assert.All(translated, order => Assert.Equal("Paid", order.Status));
            await Assert.ThrowsAsync<ArgumentException>(() => new OrderQueryService(translatedContext)
                .SearchOrdersTranslatedAsync(new OrderSearchRequest(null, request.To, request.From)));
        }
        finally { await PostgresTestSupport.CleanupAsync(data); }
    }

    [Fact]
    public async Task Pagination_OffsetAndKeysetMatchAcrossDuplicateDatesAndBoundaries()
    {
        var data = await PostgresTestSupport.CreateDataAsync();
        try
        {
            await using var context = PostgresTestSupport.CreateContext();
            var service = new OrderQueryService(context);
            var firstOffset = await service.GetOffsetPageAsync(new OrderPageRequest(0, 2, data.FirstCustomerId));
            var firstKeyset = await service.GetKeysetPageAsync(new OrderKeysetRequest(null, null, 2, data.FirstCustomerId));
            Assert.Equal(firstOffset.HasMore, firstKeyset.HasMore);
            Assert.Equal(firstOffset.Items.ToArray(), firstKeyset.Items.ToArray());
            Assert.True(firstKeyset.HasMore);
            Assert.Equal(firstKeyset.Items[0].OrderDate, firstKeyset.Items[1].OrderDate);
            Assert.True(firstKeyset.Items[0].OrderId > firstKeyset.Items[1].OrderId);

            var last = firstKeyset.Items[^1];
            var nextOffset = await service.GetOffsetPageAsync(new OrderPageRequest(2, 2, data.FirstCustomerId));
            var nextKeyset = await service.GetKeysetPageAsync(
                new OrderKeysetRequest(last.OrderDate, last.OrderId, 2, data.FirstCustomerId));
            Assert.Equal(nextOffset.HasMore, nextKeyset.HasMore);
            Assert.Equal(nextOffset.Items.ToArray(), nextKeyset.Items.ToArray());
            Assert.False(nextKeyset.HasMore);
            Assert.Single(nextKeyset.Items);

            Assert.Empty((await service.GetOffsetPageAsync(new OrderPageRequest(3, 2, data.FirstCustomerId))).Items);
            Assert.Empty((await service.GetKeysetPageAsync(new OrderKeysetRequest(
                nextKeyset.Items[0].OrderDate, nextKeyset.Items[0].OrderId, 2, data.FirstCustomerId))).Items);
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetKeysetPageAsync(
                new OrderKeysetRequest(last.OrderDate, null, 2, data.FirstCustomerId)));
        }
        finally { await PostgresTestSupport.CleanupAsync(data); }
    }

    [Fact]
    public async Task Totals_PerOrderAndGroupedAggregationMatchExactDecimalResults()
    {
        var data = await PostgresTestSupport.CreateDataAsync();
        try
        {
            var orderIds = data.FirstCustomerOrderIds.Concat([int.MaxValue]).ToArray();
            var baselineCounter = new SqlCommandCounter();
            await using var baselineContext = PostgresTestSupport.CreateContext(baselineCounter);
            baselineCounter.Reset();
            var baseline = await new OrderQueryService(baselineContext).GetOrderTotalsPerOrderAsync(orderIds);
            var baselineCommands = baselineCounter.Count;

            var groupedCounter = new SqlCommandCounter();
            await using var groupedContext = PostgresTestSupport.CreateContext(groupedCounter);
            groupedCounter.Reset();
            var grouped = await new OrderQueryService(groupedContext).GetOrderTotalsGroupedAsync(orderIds);
            var groupedCommands = groupedCounter.Count;

            Assert.Equal(baseline, grouped);
            Assert.Equal(orderIds.Distinct().Count(), baselineCommands);
            Assert.Equal(1, groupedCommands);
            Assert.Equal(24.78m, baseline[0].Total);
            Assert.Equal(0.30m, baseline[1].Total);
            Assert.Equal(0m, baseline[^1].Total);
        }
        finally { await PostgresTestSupport.CleanupAsync(data); }
    }

    [Fact]
    public async Task RelatedData_RepeatedLoadsMatchOneProjectionAndCommandCountsAreExplicit()
    {
        var data = await PostgresTestSupport.CreateDataAsync();
        try
        {
            var ids = new[] { data.FirstCustomerId, data.SecondCustomerId };
            var baselineCounter = new SqlCommandCounter();
            await using var baselineContext = PostgresTestSupport.CreateContext(baselineCounter);
            baselineCounter.Reset();
            var baseline = await new OrderQueryService(baselineContext).GetRelatedDataRepeatedAsync(ids);
            var baselineCommands = baselineCounter.Count;

            var projectionCounter = new SqlCommandCounter();
            await using var projectionContext = PostgresTestSupport.CreateContext(projectionCounter);
            projectionCounter.Reset();
            var projection = await new OrderQueryService(projectionContext).GetRelatedDataProjectionAsync(ids);
            var projectionCommands = projectionCounter.Count;

            Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(projection));
            Assert.Equal(7, baselineCommands); // 1 customer + 2 order + 4 order-line/product commands.
            Assert.Equal(1, projectionCommands);
        }
        finally { await PostgresTestSupport.CleanupAsync(data); }
    }

    [Fact]
    public async Task ApiResponsePair_ReturnsEquivalentJsonForTheSameCustomer()
    {
        var data = await PostgresTestSupport.CreateDataAsync();
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Postgres"] = PostgresTestSupport.ConnectionString
                    })));
            using var client = factory.CreateClient();
            var baseline = await client.GetAsync($"/api/queries/customers/{data.FirstCustomerId}/response/baseline");
            var optimized = await client.GetAsync($"/api/queries/customers/{data.FirstCustomerId}/response/optimized");

            Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
            Assert.Equal(HttpStatusCode.OK, optimized.StatusCode);
            Assert.Equal(await baseline.Content.ReadAsStringAsync(), await optimized.Content.ReadAsStringAsync());
        }
        finally { await PostgresTestSupport.CleanupAsync(data); }
    }
}
