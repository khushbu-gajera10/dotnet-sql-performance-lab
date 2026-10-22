using DotNetSqlPerformanceLab.Core.Entities;
using DotNetSqlPerformanceLab.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DotNetSqlPerformanceLab.Infrastructure.SeedData;

public sealed class SeedDataService(PerformanceLabDbContext dbContext)
{
    private const int BatchSize = 1_000;

    public async Task<SeedRowCounts> SeedAsync(int seed, SeedDatasetProfile profile, bool reset,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var customerRows = await dbContext.Customers.LongCountAsync(cancellationToken);
        var productRows = await dbContext.Products.LongCountAsync(cancellationToken);
        var orderRows = await dbContext.Orders.LongCountAsync(cancellationToken);
        var lineRows = await dbContext.OrderLines.LongCountAsync(cancellationToken);
        SeedDataGuard.EnsureCanSeed(customerRows, productRows, orderRows, lineRows, reset);

        if (reset)
        {
            await dbContext.OrderLines.ExecuteDeleteAsync(cancellationToken);
            await dbContext.Orders.ExecuteDeleteAsync(cancellationToken);
            await dbContext.Products.ExecuteDeleteAsync(cancellationToken);
            await dbContext.Customers.ExecuteDeleteAsync(cancellationToken);
        }

        var generator = new DeterministicDatasetGenerator(seed);
        await InsertBatches(generator.Customers(profile.CustomerCount), dbContext.Customers, cancellationToken);

        var productPrices = new List<decimal>(profile.ProductCount);
        foreach (var productBatch in Batches(generator.Products(profile.ProductCount)))
        {
            productPrices.AddRange(productBatch.Select(product => product.CurrentPrice));
            dbContext.Products.AddRange(productBatch);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }

        foreach (var orderBatch in Batches(generator.Orders(profile, productPrices), BatchSize))
        {
            var orders = orderBatch.Select(item => item.Order).ToArray();
            var lines = orderBatch.SelectMany(item => item.Lines).ToArray();
            dbContext.Orders.AddRange(orders);
            dbContext.OrderLines.AddRange(lines);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }

        await SynchronizeSequences(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SeedRowCounts(
            await dbContext.Customers.LongCountAsync(cancellationToken),
            await dbContext.Products.LongCountAsync(cancellationToken),
            await dbContext.Orders.LongCountAsync(cancellationToken),
            await dbContext.OrderLines.LongCountAsync(cancellationToken));
    }

    private async Task InsertBatches<T>(IEnumerable<T> source, DbSet<T> set, CancellationToken cancellationToken)
        where T : class
    {
        foreach (var batch in Batches(source))
        {
            set.AddRange(batch);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }
    }

    private static IEnumerable<List<T>> Batches<T>(IEnumerable<T> source, int batchSize = BatchSize)
    {
        var batch = new List<T>(batchSize);
        foreach (var item in source)
        {
            batch.Add(item);
            if (batch.Count != batchSize) continue;
            yield return batch;
            batch = new List<T>(batchSize);
        }
        if (batch.Count > 0) yield return batch;
    }

    private Task SynchronizeSequences(CancellationToken cancellationToken) => dbContext.Database.ExecuteSqlRawAsync("""
        SELECT setval(pg_get_serial_sequence('customers', 'Id'), COALESCE((SELECT MAX("Id") FROM customers), 1), true);
        SELECT setval(pg_get_serial_sequence('products', 'Id'), COALESCE((SELECT MAX("Id") FROM products), 1), true);
        SELECT setval(pg_get_serial_sequence('orders', 'Id'), COALESCE((SELECT MAX("Id") FROM orders), 1), true);
        SELECT setval(pg_get_serial_sequence('order_lines', 'Id'), COALESCE((SELECT MAX("Id") FROM order_lines), 1), true);
        """, cancellationToken);
}

public sealed record SeedRowCounts(long Customers, long Products, long Orders, long OrderLines);
