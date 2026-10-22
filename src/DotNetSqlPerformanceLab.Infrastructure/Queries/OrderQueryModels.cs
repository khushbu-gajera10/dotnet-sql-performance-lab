namespace DotNetSqlPerformanceLab.Infrastructure.Queries;

public sealed record CustomerHistoryResponse(
    int CustomerId,
    string CustomerName,
    string CustomerEmail,
    IReadOnlyList<CustomerHistoryOrder> Orders);

public sealed record CustomerHistoryOrder(int OrderId, DateTimeOffset OrderDate, string Status, decimal Total);

public sealed record OrderSearchRequest(string? Status, DateTimeOffset? From, DateTimeOffset? To);

public sealed record OrderSearchItem(int OrderId, int CustomerId, DateTimeOffset OrderDate, string Status, decimal Total);

public sealed record OrderPageRequest(int Skip, int Take, int? CustomerId = null);

public sealed record OrderKeysetRequest(
    DateTimeOffset? CursorOrderDate, int? CursorOrderId, int Take, int? CustomerId = null);

public sealed record OrderPageResponse(IReadOnlyList<OrderSearchItem> Items, bool HasMore);

public sealed record OrderTotalItem(int OrderId, decimal Total);

public sealed record RelatedCustomerResponse(int CustomerId, string CustomerName, IReadOnlyList<RelatedOrder> Orders);

public sealed record RelatedOrder(
    int OrderId,
    DateTimeOffset OrderDate,
    string Status,
    decimal Total,
    IReadOnlyList<RelatedOrderLine> Lines);

public sealed record RelatedOrderLine(
    int OrderLineId,
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);
