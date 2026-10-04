using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsAlgorithmSettingsRowConfiguration : IEntityTypeConfiguration<RcsAlgorithmSettingsRow>
{
    public void Configure(EntityTypeBuilder<RcsAlgorithmSettingsRow> builder)
    {
        builder.ToTable("rcs_algorithm_settings");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SegmentPointCount).HasColumnName("segment_point_count").HasDefaultValue(4);
        builder.Property(x => x.AdvanceAfterPoints).HasColumnName("advance_after_points").HasDefaultValue(1);
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
