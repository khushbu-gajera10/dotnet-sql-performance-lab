using DotNetSqlPerformanceLab.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DotNetSqlPerformanceLab.Infrastructure.Persistence.Configurations;

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines");

        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).ValueGeneratedOnAdd();

        builder.Property(line => line.OrderId).IsRequired();
        builder.Property(line => line.ProductId).IsRequired();

        builder.Property(line => line.Quantity).IsRequired();

        builder.Property(line => line.UnitPrice)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.HasOne(line => line.Order)
            .WithMany(order => order.Lines)
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(line => line.Product)
            .WithMany(product => product.OrderLines)
            .HasForeignKey(line => line.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
