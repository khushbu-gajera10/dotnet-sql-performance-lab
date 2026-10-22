namespace DotNetSqlPerformanceLab.Core.Entities;

public sealed class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Order> Orders { get; } = new List<Order>();
}
