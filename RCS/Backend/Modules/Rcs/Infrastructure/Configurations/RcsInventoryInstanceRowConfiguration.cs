using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsInventoryInstanceRowConfiguration : IEntityTypeConfiguration<RcsInventoryInstanceRow>
{
    public void Configure(EntityTypeBuilder<RcsInventoryInstanceRow> builder)
    {
        builder.ToTable("rcs_inventory_instances");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.ItemType).HasColumnName("item_type").HasMaxLength(16).IsRequired();
        builder.Property(x => x.InstanceCode).HasColumnName("instance_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.ModelCode).HasColumnName("model_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        builder.Property(x => x.MapCode).HasColumnName("map_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.PointCode).HasColumnName("point_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.ParentInstanceCode).HasColumnName("parent_instance_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.OffsetXmm).HasColumnName("offset_x_mm");
        builder.Property(x => x.OffsetYmm).HasColumnName("offset_y_mm");
        builder.Property(x => x.OffsetZmm).HasColumnName("offset_z_mm");
        builder.Property(x => x.MetadataJson).HasColumnName("metadata_json").HasColumnType("json").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => new { x.ItemType, x.InstanceCode }).IsUnique();
        builder.HasIndex(x => new { x.MapCode, x.PointCode });
        builder.HasIndex(x => x.ParentInstanceCode);
    }
}
