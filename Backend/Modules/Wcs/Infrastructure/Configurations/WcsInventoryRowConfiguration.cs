using GrcsBackend.Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrcsBackend.Modules.Wcs.Infrastructure.Configurations;

public class WcsInventoryRowConfiguration : IEntityTypeConfiguration<WcsInventoryRow>
{
    public void Configure(EntityTypeBuilder<WcsInventoryRow> builder)
    {
        builder.ToTable("wcs_inventory");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasColumnName("code").IsRequired();
        builder.Property(x => x.Station).HasColumnName("station");
        builder.Property(x => x.HomeMark).HasColumnName("home_mark");
        builder.Property(x => x.CargoCode).HasColumnName("cargo_code");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.TaskId).HasColumnName("task_id");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.Status).HasDatabaseName("idx_inv_status");
        builder.HasIndex(x => x.TaskId).HasDatabaseName("idx_inv_task");
    }
}