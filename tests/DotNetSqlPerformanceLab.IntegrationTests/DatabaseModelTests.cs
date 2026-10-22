using DotNetSqlPerformanceLab.Core.Entities;
using DotNetSqlPerformanceLab.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotNetSqlPerformanceLab.IntegrationTests;

public sealed class DatabaseModelTests
{
    [Fact]
    public void Model_MapsRequiredFieldsUniqueKeysAndMoneyPrecision()
    {
        using var context = CreateContext();
        var model = context.Model;

        var customer = model.FindEntityType(typeof(Customer))!;
        var product = model.FindEntityType(typeof(Product))!;
        var order = model.FindEntityType(typeof(Order))!;
        var orderLine = model.FindEntityType(typeof(OrderLine))!;

        Assert.False(customer.FindProperty(nameof(Customer.Name))!.IsNullable);
        Assert.Equal(120, customer.FindProperty(nameof(Customer.Name))!.GetMaxLength());
        Assert.Contains(customer.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Customer.Email)]));
        Assert.Contains(product.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Product.SKU)]));

        AssertMoneyPrecision(product.FindProperty(nameof(Product.CurrentPrice))!);
        AssertMoneyPrecision(order.FindProperty(nameof(Order.Total))!);
        AssertMoneyPrecision(orderLine.FindProperty(nameof(OrderLine.UnitPrice))!);
    }

    [Fact]
    public void Model_MapsExpectedRelationshipsAndDeleteRules()
    {
        using var context = CreateContext();
        var model = context.Model;

        var customerOrder = model.FindEntityType(typeof(Order))!
            .FindNavigation(nameof(Order.Customer))!.ForeignKey;
        var orderCustomer = model.FindEntityType(typeof(Order))!
            .FindNavigation(nameof(Order.Lines))!.ForeignKey;
        var orderLineProduct = model.FindEntityType(typeof(OrderLine))!
            .FindNavigation(nameof(OrderLine.Product))!.ForeignKey;

        Assert.Equal(DeleteBehavior.Restrict, customerOrder.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, orderCustomer.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, orderLineProduct.DeleteBehavior);
        Assert.Equal(nameof(Order.CustomerId), customerOrder.Properties.Single().Name);
        Assert.Equal(nameof(OrderLine.OrderId), orderCustomer.Properties.Single().Name);
        Assert.Equal(nameof(OrderLine.ProductId), orderLineProduct.Properties.Single().Name);
    }

    [Fact]
    public void CreateScript_ContainsTablesUniqueConstraintsAndForeignKeys()
    {
        using var context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("customers", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("order_lines", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOREIGN KEY", script, StringComparison.OrdinalIgnoreCase);
    }

    private static PerformanceLabDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PerformanceLabDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only")
            .Options;

        return new PerformanceLabDbContext(options);
    }

    private static void AssertMoneyPrecision(Microsoft.EntityFrameworkCore.Metadata.IProperty property)
    {
        Assert.Equal(18, property.GetPrecision());
        Assert.Equal(2, property.GetScale());
        Assert.False(property.IsNullable);
    }
}
