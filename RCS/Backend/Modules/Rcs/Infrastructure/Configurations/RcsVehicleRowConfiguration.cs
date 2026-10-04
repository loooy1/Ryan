using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsVehicleRowConfiguration : IEntityTypeConfiguration<RcsVehicleRow>
{
    public void Configure(EntityTypeBuilder<RcsVehicleRow> builder)
    {
        builder.ToTable("rcs_vehicles");
        builder.HasKey(x => x.VehicleId);
        builder.Property(x => x.VehicleId).HasColumnName("vehicle_id").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Protocol).HasColumnName("protocol").HasMaxLength(32).IsRequired();
        builder.Property(x => x.OperatingMode).HasColumnName("operating_mode").HasMaxLength(16).HasDefaultValue("AUTO").IsRequired();
        builder.Property(x => x.InitialPointCode).HasColumnName("initial_point_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
