using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsMapPointRowConfiguration : IEntityTypeConfiguration<RcsMapPointRow>
{
    public void Configure(EntityTypeBuilder<RcsMapPointRow> builder)
    {
        builder.ToTable("rcs_map_points");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.MapCode).HasColumnName("map_code").IsRequired();
        builder.Property(x => x.PointCode).HasColumnName("point_code").IsRequired();
        builder.Property(x => x.PointName).HasColumnName("point_name").IsRequired();
        builder.Property(x => x.PointType).HasColumnName("point_type").IsRequired();
        builder.Property(x => x.Floor).HasColumnName("floor");
        builder.Property(x => x.X).HasColumnName("x");
        builder.Property(x => x.Y).HasColumnName("y");
        builder.Property(x => x.Z).HasColumnName("z");
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        builder.Property(x => x.MetadataJson).HasColumnName("metadata_json").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
