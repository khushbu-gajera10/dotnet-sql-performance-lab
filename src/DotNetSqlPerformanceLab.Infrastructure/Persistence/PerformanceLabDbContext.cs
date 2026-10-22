using DotNetSqlPerformanceLab.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DotNetSqlPerformanceLab.Infrastructure.Persistence;

public sealed class PerformanceLabDbContext(DbContextOptions<PerformanceLabDbContext> options)
    : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PerformanceLabDbContext).Assembly);
    }
}
