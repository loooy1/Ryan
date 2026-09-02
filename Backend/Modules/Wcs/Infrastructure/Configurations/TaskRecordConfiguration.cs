using System.Globalization;
using System.Text.Json;
using GrcsBackend.Contracts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GrcsBackend.Modules.Wcs.Infrastructure.Configurations;

public class TaskRecordConfiguration : IEntityTypeConfiguration<TaskRecord>
{
    private static DateTime ParseIsoTime(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;

    public void Configure(EntityTypeBuilder<TaskRecord> builder)
    {
        builder.ToTable("task_records");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(x => x.Stage).HasColumnName("stage").IsRequired();
        builder.Property(x => x.Time).HasColumnName("time")
            .HasConversion(
                v => v.ToString("O"),
                v => ParseIsoTime(v));
        builder.Property(x => x.Warehouse).HasColumnName("warehouse");
        builder.Property(x => x.ContainerCode).HasColumnName("container_code");
        builder.Property(x => x.CargoCode).HasColumnName("cargo_code");
        builder.Property(x => x.TaskType).HasColumnName("task_type");
        builder.Property(x => x.RouteCodes).HasColumnName("route_codes")
            .HasConversion(
                v => JsonSerializer.Serialize(v),
                v => JsonSerializer.Deserialize<List<string>>(v) ?? new List<string>())
            .Metadata.SetValueComparer(ValueComparer.CreateDefault<List<string>>(favorStructuralComparisons: false));
        builder.Property(x => x.StationCode).HasColumnName("station_code");
        builder.Property(x => x.Ok).HasColumnName("ok");
        builder.Property(x => x.StatusCode).HasColumnName("status_code");
        builder.HasIndex(x => x.TaskId).HasDatabaseName("idx_tr_task");
        builder.HasIndex(x => x.Stage).HasDatabaseName("idx_tr_stage");
    }
}