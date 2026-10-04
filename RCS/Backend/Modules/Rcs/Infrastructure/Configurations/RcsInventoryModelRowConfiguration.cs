using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsInventoryModelRowConfiguration : IEntityTypeConfiguration<RcsInventoryModelRow>
{
    public void Configure(EntityTypeBuilder<RcsInventoryModelRow> builder)
    {
        builder.ToTable("rcs_inventory_models");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.ItemType).HasColumnName("item_type").HasMaxLength(16).IsRequired();
        builder.Property(x => x.ModelCode).HasColumnName("model_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.LengthMm).HasColumnName("length_mm");
        builder.Property(x => x.WidthMm).HasColumnName("width_mm");
        builder.Property(x => x.HeightMm).HasColumnName("height_mm");
        builder.Property(x => x.WeightKg).HasColumnName("weight_kg");
        builder.Property(x => x.MaxLoadKg).HasColumnName("max_load_kg");
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        builder.Property(x => x.MetadataJson).HasColumnName("metadata_json").HasColumnType("json").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => new { x.ItemType, x.ModelCode }).IsUnique();
    }
}
