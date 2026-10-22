using DotNetSqlPerformanceLab.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DotNetSqlPerformanceLab.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(order => order.Id);
        builder.Property(order => order.Id).ValueGeneratedOnAdd();

        builder.Property(order => order.CustomerId).IsRequired();

        builder.Property(order => order.OrderDate)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(order => order.Status)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(order => order.Total)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.HasOne(order => order.Customer)
            .WithMany(customer => customer.Orders)
            .HasForeignKey(order => order.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
