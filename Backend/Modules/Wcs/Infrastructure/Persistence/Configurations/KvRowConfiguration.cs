using Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public class KvRowConfiguration : IEntityTypeConfiguration<KvRow>
{
    public void Configure(EntityTypeBuilder<KvRow> builder)
    {
        builder.ToTable("kv");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasColumnName("key");
        builder.Property(x => x.Value).HasColumnName("value").IsRequired();
    }
}