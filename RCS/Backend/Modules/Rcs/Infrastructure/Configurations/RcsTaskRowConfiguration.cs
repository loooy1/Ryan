using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Configurations;

public sealed class RcsTaskRowConfiguration : IEntityTypeConfiguration<RcsTaskRow>
{
    public void Configure(EntityTypeBuilder<RcsTaskRow> builder)
    {
        builder.ToTable("rcs_tasks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.TaskId).HasColumnName("task_id").HasMaxLength(128).IsRequired();
        builder.HasIndex(x => x.TaskId).IsUnique();
        builder.Property(x => x.GroupId).HasColumnName("group_id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.MsgTime).HasColumnName("msg_time").HasMaxLength(19).IsRequired();
        builder.Property(x => x.Warehouse).HasColumnName("warehouse").HasMaxLength(128).IsRequired();
        builder.Property(x => x.PriorityCode).HasColumnName("priority_code");
        builder.Property(x => x.TaskType).HasColumnName("task_type").HasMaxLength(128).IsRequired();
        builder.Property(x => x.ContainerCode).HasColumnName("container_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.StationCodesJson).HasColumnName("station_codes_json").IsRequired();
        builder.Property(x => x.StationActionsJson).HasColumnName("station_actions_json").IsRequired();
        builder.Property(x => x.AreaCodesJson).HasColumnName("area_codes_json").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(32).IsRequired();
        builder.Property(x => x.VehicleId).HasColumnName("vehicle_id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.RequestedVehicleId).HasColumnName("requested_vehicle_id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.MapCode).HasColumnName("map_code").HasMaxLength(128).IsRequired();
        builder.Property(x => x.MapVersion).HasColumnName("map_version");
        builder.Property(x => x.RouteJson).HasColumnName("route_json").IsRequired();
        builder.Property(x => x.Message).HasColumnName("message").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at");
        builder.HasIndex(x => new { x.Status, x.PriorityCode, x.Id });
    }
}
