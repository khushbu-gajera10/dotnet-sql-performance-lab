using DotNetSqlPerformanceLab.Infrastructure.SeedData;
using Xunit;

namespace DotNetSqlPerformanceLab.UnitTests;

public sealed class DeterministicDatasetGeneratorTests
{
    [Fact]
    public void SameSeedAndProfile_ProduceSameRowsAndIds()
    {
        var profile = SeedDatasetProfile.ForOrderCount(10_000);
        var first = new DeterministicDatasetGenerator(20261019);
        var second = new DeterministicDatasetGenerator(20261019);

        Assert.Equal(first.Customers(profile.CustomerCount).Take(30).Select(customer =>
            (customer.Id, customer.Name, customer.Email, customer.CreatedAt)),
            second.Customers(profile.CustomerCount).Take(30).Select(customer =>
                (customer.Id, customer.Name, customer.Email, customer.CreatedAt)));
        Assert.Equal(first.Products(profile.ProductCount).Select(product =>
            (product.Id, product.SKU, product.Name, product.Category, product.CurrentPrice)),
            second.Products(profile.ProductCount).Select(product =>
                (product.Id, product.SKU, product.Name, product.Category, product.CurrentPrice)));
        var prices = first.Products(profile.ProductCount).Select(product => product.CurrentPrice).ToArray();
        var firstOrders = first.Orders(profile, prices).Take(25).ToArray();
        var secondOrders = second.Orders(profile, prices).Take(25).ToArray();
        Assert.Equal(firstOrders.Select(Describe), secondOrders.Select(Describe));
    }

    [Fact]
    public void GeneratedOrders_HaveValidReferencesLineCountsAndTotals()
    {
        var profile = SeedDatasetProfile.ForOrderCount(10_000);
        var generator = new DeterministicDatasetGenerator(14);
        var productPrices = generator.Products(profile.ProductCount).Select(product => product.CurrentPrice).ToArray();

        foreach (var (order, lines) in generator.Orders(profile, productPrices).Take(100))
        {
            Assert.InRange(order.CustomerId, 1, profile.CustomerCount);
            Assert.InRange(lines.Count, 3, 5);
            Assert.All(lines, line =>
            {
                Assert.Equal(order.Id, line.OrderId);
                Assert.InRange(line.ProductId, 1, profile.ProductCount);
                Assert.InRange(line.Quantity, 1, 5);
                Assert.Equal(productPrices[line.ProductId - 1], line.UnitPrice);
            });
            Assert.Equal(lines.Sum(line => line.Quantity * line.UnitPrice), order.Total);
            Assert.Contains(order.Status, new[] { "Pending", "Paid", "Shipped", "Cancelled", "Completed" });
            Assert.InRange(order.OrderDate, new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }
    }

    [Fact]
    public void StandardSmallProfile_UsesEveryCustomerAndProduct()
    {
        var profile = SeedDatasetProfile.ForOrderCount(10_000);
        var generator = new DeterministicDatasetGenerator(20261019);
        var prices = generator.Products(profile.ProductCount).Select(product => product.CurrentPrice).ToArray();
        var generated = generator.Orders(profile, prices).ToArray();

        Assert.Equal(profile.CustomerCount, generated.Select(item => item.Order.CustomerId).Distinct().Count());
        Assert.Equal(profile.ProductCount,
            generated.SelectMany(item => item.Lines).Select(line => line.ProductId).Distinct().Count());
    }

    [Theory]
    [InlineData(10_000, 2_000, 500)]
    [InlineData(100_000, 20_000, 2_000)]
    public void Profiles_ExposeApprovedDatasetSizes(int orders, int customers, int products)
    {
        Assert.Equal(new SeedDatasetProfile(orders, customers, products), SeedDatasetProfile.ForOrderCount(orders));
    }

    [Fact]
    public void UnsupportedOrderCount_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SeedDatasetProfile.ForOrderCount(5_000));
    }

    [Fact]
    public void ExistingData_IsRejectedUnlessResetWasExplicit()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SeedDataGuard.EnsureCanSeed(1, 0, 0, 0, reset: false));
        Assert.Contains("No rows were changed", exception.Message);
        Assert.Contains("--reset", exception.Message);
        SeedDataGuard.EnsureCanSeed(1, 1, 1, 1, reset: true);
        SeedDataGuard.EnsureCanSeed(0, 0, 0, 0, reset: false);
    }

    private static string Describe((DotNetSqlPerformanceLab.Core.Entities.Order Order,
        IReadOnlyList<DotNetSqlPerformanceLab.Core.Entities.OrderLine> Lines) item) => string.Join("|",
        item.Order.Id, item.Order.CustomerId, item.Order.OrderDate, item.Order.Status, item.Order.Total,
        string.Join(";", item.Lines.Select(line =>
            $"{line.Id},{line.OrderId},{line.ProductId},{line.Quantity},{line.UnitPrice}")));
}
