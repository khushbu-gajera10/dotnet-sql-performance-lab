using DotNetSqlPerformanceLab.Core.Entities;
using DotNetSqlPerformanceLab.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DotNetSqlPerformanceLab.Infrastructure.Queries;

public sealed class OrderQueryService(PerformanceLabDbContext dbContext)
{
    public async Task<CustomerHistoryResponse?> GetCustomerHistoryTrackedAsync(
        int customerId, CancellationToken cancellationToken = default)
    {
        var customer = await dbContext.Customers
            .Include(item => item.Orders.OrderByDescending(order => order.OrderDate).ThenByDescending(order => order.Id))
            .SingleOrDefaultAsync(item => item.Id == customerId, cancellationToken);

        return customer is null ? null : new CustomerHistoryResponse(
            customer.Id,
            customer.Name,
            customer.Email,
            customer.Orders.Select(ToHistoryOrder).ToArray());
    }

    public Task<CustomerHistoryResponse?> GetCustomerHistoryProjectionAsync(
        int customerId, CancellationToken cancellationToken = default) =>
        dbContext.Customers.AsNoTracking()
            .Where(customer => customer.Id == customerId)
            .Select(customer => new CustomerHistoryResponse(
                customer.Id,
                customer.Name,
                customer.Email,
                customer.Orders
                    .OrderByDescending(order => order.OrderDate)
                    .ThenByDescending(order => order.Id)
                    .Select(order => new CustomerHistoryOrder(order.Id, order.OrderDate, order.Status, order.Total))
                    .ToArray()))
            .SingleOrDefaultAsync(cancellationToken);

    // Deliberately materializes the candidate order set before applying the same filters and ordering in memory.
    public async Task<IReadOnlyList<OrderSearchItem>> SearchOrdersBaselineAsync(
        OrderSearchRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSearch(request);
        var candidates = await dbContext.Orders.AsNoTracking()
            .Select(order => new OrderSearchItem(order.Id, order.CustomerId, order.OrderDate, order.Status, order.Total))
            .ToListAsync(cancellationToken);

        return ApplySearch(candidates, request).ToArray();
    }

    public async Task<IReadOnlyList<OrderSearchItem>> SearchOrdersTranslatedAsync(
        OrderSearchRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSearch(request);
        var query = dbContext.Orders.AsNoTracking().AsQueryable();
        if (request.Status is not null)
            query = query.Where(order => order.Status == request.Status);
        if (request.From is not null)
            query = query.Where(order => order.OrderDate >= request.From.Value);
        if (request.To is not null)
            query = query.Where(order => order.OrderDate < request.To.Value);

        return await query.OrderByDescending(order => order.OrderDate).ThenByDescending(order => order.Id)
            .Select(order => new OrderSearchItem(order.Id, order.CustomerId, order.OrderDate, order.Status, order.Total))
            .ToListAsync(cancellationToken);
    }

    public async Task<OrderPageResponse> GetOffsetPageAsync(
        OrderPageRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePage(request.Skip, request.Take);
        ValidateCustomerId(request.CustomerId);
        var rows = await OrderedOrders(request.CustomerId).Skip(request.Skip).Take(request.Take + 1).ToListAsync(cancellationToken);
        return ToPage(rows, request.Take);
    }

    public async Task<OrderPageResponse> GetKeysetPageAsync(
        OrderKeysetRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePage(0, request.Take);
        ValidateCustomerId(request.CustomerId);
        if (request.CursorOrderDate.HasValue != request.CursorOrderId.HasValue)
            throw new ArgumentException("Both keyset cursor fields must be supplied together.", nameof(request));

        var query = FilteredOrders(request.CustomerId);
        if (request.CursorOrderDate is { } date && request.CursorOrderId is { } id)
        {
            query = query.Where(order => order.OrderDate < date ||
                (order.OrderDate == date && order.Id < id));
        }

        var rows = await query.OrderByDescending(order => order.OrderDate).ThenByDescending(order => order.Id)
            .Select(order => new OrderSearchItem(order.Id, order.CustomerId, order.OrderDate, order.Status, order.Total))
            .Take(request.Take + 1).ToListAsync(cancellationToken);
        return ToPage(rows, request.Take);
    }

    public async Task<IReadOnlyList<OrderTotalItem>> GetOrderTotalsPerOrderAsync(
        IReadOnlyList<int> orderIds, CancellationToken cancellationToken = default)
    {
        ValidateIds(orderIds, nameof(orderIds));
        var totals = new List<OrderTotalItem>(orderIds.Distinct().Count());
        foreach (var orderId in orderIds.Distinct())
        {
            var total = await dbContext.OrderLines.Where(line => line.OrderId == orderId)
                .Select(line => line.Quantity * line.UnitPrice)
                .SumAsync(cancellationToken);
            totals.Add(new OrderTotalItem(orderId, total));
        }
        return totals;
    }

    public async Task<IReadOnlyList<OrderTotalItem>> GetOrderTotalsGroupedAsync(
        IReadOnlyList<int> orderIds, CancellationToken cancellationToken = default)
    {
        ValidateIds(orderIds, nameof(orderIds));
        var orderedIds = orderIds.Distinct().ToArray();
        var grouped = await dbContext.OrderLines.AsNoTracking()
            .Where(line => orderedIds.Contains(line.OrderId))
            .GroupBy(line => line.OrderId)
            .Select(group => new OrderTotalItem(group.Key, group.Sum(line => line.Quantity * line.UnitPrice)))
            .ToDictionaryAsync(item => item.OrderId, item => item.Total, cancellationToken);
        return orderedIds.Select(id => new OrderTotalItem(id, grouped.GetValueOrDefault(id))).ToArray();
    }

    // Deliberately repeats related-data queries: one customer query, one order query per customer,
    // and one order-line/product query per order. Lazy-loading proxies are not enabled globally.
    public async Task<IReadOnlyList<RelatedCustomerResponse>> GetRelatedDataRepeatedAsync(
        IReadOnlyList<int> customerIds, CancellationToken cancellationToken = default)
    {
        ValidateIds(customerIds, nameof(customerIds));
        var customers = await dbContext.Customers.AsNoTracking()
            .Where(customer => customerIds.Contains(customer.Id))
            .OrderBy(customer => customer.Id)
            .Select(customer => new { customer.Id, customer.Name })
            .ToListAsync(cancellationToken);

        var result = new List<RelatedCustomerResponse>(customers.Count);
        foreach (var customer in customers)
        {
            var orders = await dbContext.Orders.AsNoTracking()
                .Where(order => order.CustomerId == customer.Id)
                .OrderByDescending(order => order.OrderDate).ThenByDescending(order => order.Id)
                .Select(order => new { order.Id, order.OrderDate, order.Status, order.Total })
                .ToListAsync(cancellationToken);

            var relatedOrders = new List<RelatedOrder>(orders.Count);
            foreach (var order in orders)
            {
                var lines = await dbContext.OrderLines.AsNoTracking()
                    .Where(line => line.OrderId == order.Id)
                    .OrderBy(line => line.Id)
                    .Select(line => new RelatedOrderLine(
                        line.Id, line.ProductId, line.Product.Name, line.Quantity, line.UnitPrice))
                    .ToListAsync(cancellationToken);
                relatedOrders.Add(new RelatedOrder(order.Id, order.OrderDate, order.Status, order.Total, lines));
            }
            result.Add(new RelatedCustomerResponse(customer.Id, customer.Name, relatedOrders));
        }
        return result;
    }

    public async Task<IReadOnlyList<RelatedCustomerResponse>> GetRelatedDataProjectionAsync(
        IReadOnlyList<int> customerIds, CancellationToken cancellationToken = default)
    {
        ValidateIds(customerIds, nameof(customerIds));
        var ids = customerIds.Distinct().ToArray();
        var rows = await (
            from customer in dbContext.Customers.AsNoTracking()
            where ids.Contains(customer.Id)
            join order in dbContext.Orders.AsNoTracking() on customer.Id equals order.CustomerId into customerOrders
            from order in customerOrders.DefaultIfEmpty()
            join line in dbContext.OrderLines.AsNoTracking() on order!.Id equals line.OrderId into orderLines
            from line in orderLines.DefaultIfEmpty()
            join product in dbContext.Products.AsNoTracking() on line!.ProductId equals product.Id into lineProducts
            from product in lineProducts.DefaultIfEmpty()
            orderby customer.Id, order!.OrderDate descending, order.Id descending, line!.Id
            select new RelatedDataRow(
                customer.Id, customer.Name,
                order == null ? null : (int?)order.Id,
                order == null ? null : (DateTimeOffset?)order.OrderDate,
                order == null ? null : order.Status,
                order == null ? null : (decimal?)order.Total,
                line == null ? null : (int?)line.Id,
                line == null ? null : (int?)line.ProductId,
                product == null ? null : product.Name,
                line == null ? null : (int?)line.Quantity,
                line == null ? null : (decimal?)line.UnitPrice))
            .ToListAsync(cancellationToken);

        return rows.GroupBy(row => new { row.CustomerId, row.CustomerName })
            .OrderBy(group => group.Key.CustomerId)
            .Select(customerGroup => new RelatedCustomerResponse(
                customerGroup.Key.CustomerId,
                customerGroup.Key.CustomerName,
                customerGroup.Where(row => row.OrderId.HasValue)
                    .GroupBy(row => new { row.OrderId, row.OrderDate, row.Status, row.Total })
                    .Select(orderGroup => new RelatedOrder(
                        orderGroup.Key.OrderId!.Value,
                        orderGroup.Key.OrderDate!.Value,
                        orderGroup.Key.Status!,
                        orderGroup.Key.Total!.Value,
                        orderGroup.Where(row => row.OrderLineId.HasValue)
                            .OrderBy(row => row.OrderLineId)
                            .Select(row => new RelatedOrderLine(
                                row.OrderLineId!.Value, row.ProductId!.Value, row.ProductName!,
                                row.Quantity!.Value, row.UnitPrice!.Value))
                            .ToArray()))
                    .ToArray()))
            .ToArray();
    }

    private IQueryable<OrderSearchItem> OrderedOrders(int? customerId)
    {
        return FilteredOrders(customerId).OrderByDescending(order => order.OrderDate).ThenByDescending(order => order.Id)
            .Select(order => new OrderSearchItem(order.Id, order.CustomerId, order.OrderDate, order.Status, order.Total));
    }

    private IQueryable<Order> FilteredOrders(int? customerId)
    {
        var query = dbContext.Orders.AsNoTracking();
        if (customerId is not null)
            query = query.Where(order => order.CustomerId == customerId.Value);
        return query;
    }

    private static CustomerHistoryOrder ToHistoryOrder(Order order) =>
        new(order.Id, order.OrderDate, order.Status, order.Total);

    private static IEnumerable<OrderSearchItem> ApplySearch(
        IEnumerable<OrderSearchItem> source, OrderSearchRequest request) => source
        .Where(order => request.Status is null || order.Status == request.Status)
        .Where(order => request.From is null || order.OrderDate >= request.From.Value)
        .Where(order => request.To is null || order.OrderDate < request.To.Value)
        .OrderByDescending(order => order.OrderDate)
        .ThenByDescending(order => order.OrderId);

    private static OrderPageResponse ToPage(List<OrderSearchItem> rows, int take)
    {
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new OrderPageResponse(rows, hasMore);
    }

    private static void ValidateSearch(OrderSearchRequest request)
    {
        if (request.From is not null && request.To is not null && request.From >= request.To)
            throw new ArgumentException("The 'from' timestamp must be earlier than the exclusive 'to' timestamp.", nameof(request));
    }

    private static void ValidatePage(int skip, int take)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(take), "Take must be between 1 and 200.");
    }

    private static void ValidateCustomerId(int? customerId)
    {
        if (customerId is < 1) throw new ArgumentOutOfRangeException(nameof(customerId));
    }

    private static void ValidateIds(IReadOnlyList<int> ids, string parameterName)
    {
        if (ids.Count is < 1 or > 500 || ids.Any(id => id < 1))
            throw new ArgumentOutOfRangeException(parameterName, "Supply between 1 and 500 positive IDs.");
    }

    private sealed record RelatedDataRow(
        int CustomerId, string CustomerName, int? OrderId, DateTimeOffset? OrderDate,
        string? Status, decimal? Total, int? OrderLineId, int? ProductId,
        string? ProductName, int? Quantity, decimal? UnitPrice);
}
