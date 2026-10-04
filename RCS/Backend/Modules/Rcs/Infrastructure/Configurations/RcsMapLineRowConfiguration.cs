using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsMapLineRowConfiguration : IEntityTypeConfiguration<RcsMapLineRow>
{
    public void Configure(EntityTypeBuilder<RcsMapLineRow> builder)
    {
        builder.ToTable("rcs_map_lines");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.MapCode).HasColumnName("map_code").IsRequired();
        builder.Property(x => x.LineCode).HasColumnName("line_code").IsRequired();
        builder.Property(x => x.FromPointCode).HasColumnName("from_point_code").IsRequired();
        builder.Property(x => x.ToPointCode).HasColumnName("to_point_code").IsRequired();
        builder.Property(x => x.Distance).HasColumnName("distance");
        builder.Property(x => x.Direction).HasColumnName("direction").IsRequired();
        builder.Property(x => x.MaxSpeed).HasColumnName("max_speed");
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        builder.Property(x => x.MetadataJson).HasColumnName("metadata_json").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
