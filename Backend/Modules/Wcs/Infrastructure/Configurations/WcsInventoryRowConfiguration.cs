using Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WCSBackend.Modules.Wcs.Infrastructure.Configurations;

public class WcsSlotRowConfiguration : IEntityTypeConfiguration<WcsSlotRow>
{
    public void Configure(EntityTypeBuilder<WcsSlotRow> builder)
    {
        builder.ToTable("wcs_slots");
        builder.HasKey(x => x.Mark);
        builder.Property(x => x.Mark).HasColumnName("mark").IsRequired();
        builder.Property(x => x.PalletCode).HasColumnName("pallet_code");
        builder.Property(x => x.PalletStatus).HasColumnName("pallet_status");
        builder.Property(x => x.CargoCode).HasColumnName("cargo_code");
        builder.Property(x => x.CargoStatus).HasColumnName("cargo_status");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.PalletCode).HasDatabaseName("idx_slot_pallet");
        builder.HasIndex(x => x.CargoCode).HasDatabaseName("idx_slot_cargo");
    }
}