namespace DotNetSqlPerformanceLab.Core.Entities;

public sealed class Order
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public DateTimeOffset OrderDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public Customer Customer { get; set; } = null!;

    public ICollection<OrderLine> Lines { get; } = new List<OrderLine>();
}
