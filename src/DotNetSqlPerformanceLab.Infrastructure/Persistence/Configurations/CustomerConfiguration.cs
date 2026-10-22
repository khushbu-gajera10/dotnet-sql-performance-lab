using DotNetSqlPerformanceLab.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DotNetSqlPerformanceLab.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");

        builder.HasKey(customer => customer.Id);
        builder.Property(customer => customer.Id).ValueGeneratedOnAdd();

        builder.Property(customer => customer.Name)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(customer => customer.Email)
            .HasMaxLength(254)
            .IsRequired();

        builder.HasIndex(customer => customer.Email).IsUnique();

        builder.Property(customer => customer.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
