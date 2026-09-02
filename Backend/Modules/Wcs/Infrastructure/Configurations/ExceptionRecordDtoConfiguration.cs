using GrcsBackend.Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrcsBackend.Modules.Wcs.Infrastructure.Configurations;

public class ExceptionRecordDtoConfiguration : IEntityTypeConfiguration<ExceptionRecordDto>
{
    public void Configure(EntityTypeBuilder<ExceptionRecordDto> builder)
    {
        builder.ToTable("exception_records");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.HappenedAt).HasColumnName("happened_at").IsRequired();
        builder.Property(x => x.VehicleCode).HasColumnName("vehicle_code");
        builder.Property(x => x.Phenomenon).HasColumnName("phenomenon").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.Progress).HasColumnName("progress");
        builder.Property(x => x.ResponsibleDept).HasColumnName("responsible_dept");
        builder.Property(x => x.Status).HasColumnName("status");
        builder.Property(x => x.Project).HasColumnName("project");
        builder.Property(x => x.ReproducedAt).HasColumnName("reproduced_at");
        builder.Property(x => x.ReproduceCount).HasColumnName("reproduce_count");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}