using DotNetSqlPerformanceLab.Infrastructure.Queries;

namespace DotNetSqlPerformanceLab.Api;

public static class PerformanceEndpoints
{
    public static IEndpointRouteBuilder MapPerformanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/queries");

        MapHistory(group, "history/baseline", (service, id, token) => service.GetCustomerHistoryTrackedAsync(id, token));
        MapHistory(group, "history/optimized", (service, id, token) => service.GetCustomerHistoryProjectionAsync(id, token));
        MapHistory(group, "response/baseline", (service, id, token) => service.GetCustomerHistoryTrackedAsync(id, token));
        MapHistory(group, "response/optimized", (service, id, token) => service.GetCustomerHistoryProjectionAsync(id, token));

        group.MapGet("orders/search/baseline", async (
            string? status, DateTimeOffset? from, DateTimeOffset? to,
            OrderQueryService service, CancellationToken cancellationToken) =>
            await Execute(() => service.SearchOrdersBaselineAsync(
                new OrderSearchRequest(status, from, to), cancellationToken)));
        group.MapGet("orders/search/optimized", async (
            string? status, DateTimeOffset? from, DateTimeOffset? to,
            OrderQueryService service, CancellationToken cancellationToken) =>
            await Execute(() => service.SearchOrdersTranslatedAsync(
                new OrderSearchRequest(status, from, to), cancellationToken)));

        group.MapGet("orders/page/offset", async (
            int skip, int take, int? customerId, OrderQueryService service, CancellationToken cancellationToken) =>
            await Execute(() => service.GetOffsetPageAsync(new OrderPageRequest(skip, take, customerId), cancellationToken)));
        group.MapGet("orders/page/keyset", async (
            DateTimeOffset? cursorOrderDate, int? cursorOrderId, int take, int? customerId,
            OrderQueryService service, CancellationToken cancellationToken) =>
            await Execute(() => service.GetKeysetPageAsync(
                new OrderKeysetRequest(cursorOrderDate, cursorOrderId, take, customerId), cancellationToken)));

        group.MapGet("orders/totals/baseline", async (
            int[] orderIds, OrderQueryService service, CancellationToken cancellationToken) =>
            await Execute(() => service.GetOrderTotalsPerOrderAsync(orderIds, cancellationToken)));
        group.MapGet("orders/totals/optimized", async (
            int[] orderIds, OrderQueryService service, CancellationToken cancellationToken) =>
            await Execute(() => service.GetOrderTotalsGroupedAsync(orderIds, cancellationToken)));

        group.MapGet("customers/related/baseline", async (
            int[] customerIds, OrderQueryService service, CancellationToken cancellationToken) =>
            await Execute(() => service.GetRelatedDataRepeatedAsync(customerIds, cancellationToken)));
        group.MapGet("customers/related/optimized", async (
            int[] customerIds, OrderQueryService service, CancellationToken cancellationToken) =>
            await Execute(() => service.GetRelatedDataProjectionAsync(customerIds, cancellationToken)));

        return endpoints;
    }

    private static void MapHistory(
        RouteGroupBuilder group,
        string route,
        Func<OrderQueryService, int, CancellationToken, Task<CustomerHistoryResponse?>> query) =>
        group.MapGet($"customers/{{customerId:int}}/{route}", async (
            int customerId, OrderQueryService service, CancellationToken cancellationToken) =>
        {
            var result = await query(service, customerId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

    private static async Task<IResult> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}
