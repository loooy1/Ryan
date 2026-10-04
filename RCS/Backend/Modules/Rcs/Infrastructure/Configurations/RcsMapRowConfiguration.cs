using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsMapRowConfiguration : IEntityTypeConfiguration<RcsMapRow>
{
    public void Configure(EntityTypeBuilder<RcsMapRow> builder)
    {
        builder.ToTable("rcs_maps");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.MapCode).HasColumnName("map_code").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.SceneName).HasColumnName("scene_name").IsRequired();
        builder.Property(x => x.SourceType).HasColumnName("source_type").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.CoordinateSystem).HasColumnName("coordinate_system").IsRequired();
        builder.Property(x => x.OriginX).HasColumnName("origin_x");
        builder.Property(x => x.OriginY).HasColumnName("origin_y");
        builder.Property(x => x.OriginZ).HasColumnName("origin_z");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.Description).HasColumnName("description").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
