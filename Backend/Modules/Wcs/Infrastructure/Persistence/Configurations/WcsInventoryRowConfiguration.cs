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
        builder.Property(x => x.SiteType).HasColumnName("site_type");
        builder.Property(x => x.Floor).HasColumnName("floor");
        builder.Property(x => x.X).HasColumnName("x");
        builder.Property(x => x.Y).HasColumnName("y");
        builder.Property(x => x.PalletCode).HasColumnName("pallet_code");
        builder.Property(x => x.PalletStatus).HasColumnName("pallet_status");
        builder.Property(x => x.CargoCode).HasColumnName("cargo_code");
        builder.Property(x => x.CargoStatus).HasColumnName("cargo_status");
        builder.Property(x => x.TaskLockId).HasColumnName("task_lock_id");
        builder.Property(x => x.ParentStationCode).HasColumnName("parent_station_code");
        builder.Property(x => x.SelectionStatus).HasColumnName("selection_status");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.PalletCode).HasDatabaseName("idx_slot_pallet");
        builder.HasIndex(x => x.CargoCode).HasDatabaseName("idx_slot_cargo");
    }
}
