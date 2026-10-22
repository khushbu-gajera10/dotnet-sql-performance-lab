namespace DotNetSqlPerformanceLab.Core.Entities;

public sealed class Product
{
    public int Id { get; set; }

    public string SKU { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public decimal CurrentPrice { get; set; }

    public ICollection<OrderLine> OrderLines { get; } = new List<OrderLine>();
}
