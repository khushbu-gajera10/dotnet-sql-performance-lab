using DotNetSqlPerformanceLab.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DotNetSqlPerformanceLab.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");

        builder.HasKey(product => product.Id);
        builder.Property(product => product.Id).ValueGeneratedOnAdd();

        builder.Property(product => product.SKU)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(product => product.SKU).IsUnique();

        builder.Property(product => product.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(product => product.Category)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(product => product.CurrentPrice)
            .HasPrecision(18, 2)
            .IsRequired();
    }
}
