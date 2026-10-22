using DotNetSqlPerformanceLab.Core.Entities;

namespace DotNetSqlPerformanceLab.Infrastructure.SeedData;

/// <summary>Generates repeatable portfolio data using SplitMix64 (fixed constants and arithmetic).</summary>
public sealed class DeterministicDatasetGenerator(int seed)
{
    private static readonly string[] Categories = ["Books", "Electronics", "Home", "Office", "Sports"];
    private static readonly string[] Statuses = ["Pending", "Paid", "Shipped", "Cancelled", "Completed"];
    private static readonly string[] GivenNames = ["Alex", "Casey", "Jordan", "Morgan", "Riley", "Taylor"];
    private static readonly string[] FamilyNames = ["Anderson", "Chen", "Patel", "Rivera", "Singh", "Wilson"];
    private static readonly DateTimeOffset CustomerStart = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OrderStart = new(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int HistoricalDays = 1_096;

    public IEnumerable<Customer> Customers(int count)
    {
        var random = new StableRandom(StreamSeed(0x435553544F4D4552));
        for (var id = 1; id <= count; id++)
        {
            yield return new Customer
            {
                Id = id,
                Name = $"{GivenNames[random.Next(GivenNames.Length)]} {FamilyNames[random.Next(FamilyNames.Length)]}",
                Email = $"customer{id:D6}@example.test",
                CreatedAt = CustomerStart.AddDays(random.Next(1_826)).AddSeconds(random.Next(86_400))
            };
        }
    }

    public IEnumerable<Product> Products(int count)
    {
        var random = new StableRandom(StreamSeed(0x50524F4455435453));
        for (var id = 1; id <= count; id++)
        {
            var category = Categories[random.Next(Categories.Length)];
            yield return new Product
            {
                Id = id,
                SKU = $"SKU-{id:D6}",
                Name = $"{category} Product {id:D4}",
                Category = category,
                CurrentPrice = random.Next(10_000, 250_000) / 100m
            };
        }
    }

    public IEnumerable<(Order Order, IReadOnlyList<OrderLine> Lines)> Orders(
        SeedDatasetProfile profile,
        IReadOnlyList<decimal> productPrices)
    {
        if (productPrices.Count != profile.ProductCount)
            throw new ArgumentException("Product prices must match the selected profile.", nameof(productPrices));

        var random = new StableRandom(StreamSeed(0x4F52444552534C4E));
        var assignmentRandom = new StableRandom(StreamSeed(0x41535349474E4D45));
        var customerOffset = assignmentRandom.Next(profile.CustomerCount);
        var productOffset = assignmentRandom.Next(profile.ProductCount);
        var lineId = 1;
        for (var orderId = 1; orderId <= profile.OrderCount; orderId++)
        {
            var count = 3 + (random.Next(3));
            var lines = new OrderLine[count];
            decimal total = 0;
            for (var index = 0; index < count; index++)
            {
                var productId = (int)(((long)lineId - 1 + productOffset) % profile.ProductCount) + 1;
                var quantity = random.Next(1, 6);
                var unitPrice = productPrices[productId - 1];
                lines[index] = new OrderLine
                {
                    Id = lineId++, OrderId = orderId, ProductId = productId,
                    Quantity = quantity, UnitPrice = unitPrice
                };
                total += quantity * unitPrice;
            }

            yield return (new Order
            {
                Id = orderId,
                CustomerId = (int)(((long)orderId - 1 + customerOffset) % profile.CustomerCount) + 1,
                OrderDate = OrderStart.AddDays(random.Next(HistoricalDays)).AddSeconds(random.Next(86_400)),
                Status = Statuses[random.Next(Statuses.Length)],
                Total = total
            }, lines);
        }
    }

    private ulong StreamSeed(ulong stream) => unchecked((ulong)(long)seed) ^ stream;

    private struct StableRandom(ulong state)
    {
        private ulong _state = state;

        public int Next(int exclusiveMaximum) => (int)(NextUInt64() % (uint)exclusiveMaximum);

        public int Next(int inclusiveMinimum, int exclusiveMaximum) =>
            inclusiveMinimum + Next(exclusiveMaximum - inclusiveMinimum);

        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
            return value ^ (value >> 31);
        }
    }
}
