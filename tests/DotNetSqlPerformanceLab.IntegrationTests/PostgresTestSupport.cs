using DotNetSqlPerformanceLab.Core.Entities;
using DotNetSqlPerformanceLab.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace DotNetSqlPerformanceLab.IntegrationTests;

internal static class PostgresTestSupport
{
    public static string ConnectionString
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("ConnectionStrings__PostgresTest");
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    "PostgreSQL query integration tests require ConnectionStrings__PostgresTest targeting a dedicated *_test database.");

            var builder = new NpgsqlConnectionStringBuilder(value);
            if (!(builder.Database ?? string.Empty).EndsWith("_test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Refusing database integration tests: the target database name must end with '_test'.");

            return builder.ConnectionString;
        }
    }

    public static PerformanceLabDbContext CreateContext(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<PerformanceLabDbContext>().UseNpgsql(ConnectionString);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return new PerformanceLabDbContext(builder.Options);
    }

    public static async Task<TestData> CreateDataAsync(int ordersForFirstCustomer = 3)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var first = new Customer
        {
            Name = "Query Test A", Email = $"query-a-{suffix}@example.test",
            CreatedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var second = new Customer
        {
            Name = "Query Test B", Email = $"query-b-{suffix}@example.test",
            CreatedAt = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };
        var firstProduct = new Product { SKU = $"T-{suffix}-1", Name = "Query Test Product 1", Category = "Books", CurrentPrice = 12.34m };
        var secondProduct = new Product { SKU = $"T-{suffix}-2", Name = "Query Test Product 2", Category = "Office", CurrentPrice = 0.10m };
        context.AddRange(first, second, firstProduct, secondProduct);
        await context.SaveChangesAsync();

        var tiedDate = new DateTimeOffset(2001, 2, 3, 10, 0, 0, TimeSpan.Zero);
        var firstOrders = new List<Order>();
        for (var index = 0; index < ordersForFirstCustomer; index++)
        {
            firstOrders.Add(new Order
            {
                CustomerId = first.Id,
                OrderDate = index < 2 ? tiedDate : tiedDate.AddDays(-1),
                Status = index == 2 ? "Shipped" : "Paid",
                Total = index == 1 ? 0.30m : 24.78m
            });
        }
        var secondOrder = new Order
        {
            CustomerId = second.Id, OrderDate = tiedDate, Status = "Cancelled", Total = 12.34m
        };
        firstOrders.Add(secondOrder);
        context.Orders.AddRange(firstOrders);
        await context.SaveChangesAsync();

        var lines = new List<OrderLine>();
        for (var index = 0; index < ordersForFirstCustomer; index++)
        {
            var order = firstOrders[index];
            if (index == 1)
                lines.Add(new OrderLine { OrderId = order.Id, ProductId = secondProduct.Id, Quantity = 3, UnitPrice = 0.10m });
            else
            {
                lines.Add(new OrderLine { OrderId = order.Id, ProductId = firstProduct.Id, Quantity = 2, UnitPrice = 12.34m });
                lines.Add(new OrderLine { OrderId = order.Id, ProductId = secondProduct.Id, Quantity = 1, UnitPrice = 0.10m });
            }
        }
        lines.Add(new OrderLine { OrderId = secondOrder.Id, ProductId = firstProduct.Id, Quantity = 1, UnitPrice = 12.34m });
        context.OrderLines.AddRange(lines);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        return new TestData(first.Id, second.Id, firstProduct.Id, secondProduct.Id,
            firstOrders.Select(order => order.Id).ToArray());
    }

    public static async Task CleanupAsync(TestData data)
    {
        await using var context = CreateContext();
        await context.Orders.Where(order => order.CustomerId == data.FirstCustomerId || order.CustomerId == data.SecondCustomerId)
            .ExecuteDeleteAsync();
        await context.Customers.Where(customer => customer.Id == data.FirstCustomerId || customer.Id == data.SecondCustomerId)
            .ExecuteDeleteAsync();
        await context.Products.Where(product => product.Id == data.FirstProductId || product.Id == data.SecondProductId)
            .ExecuteDeleteAsync();
    }
}

internal sealed record TestData(
    int FirstCustomerId,
    int SecondCustomerId,
    int FirstProductId,
    int SecondProductId,
    int[] FirstCustomerOrderIds);
