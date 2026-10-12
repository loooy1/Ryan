using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backend.Shared.Infrastructure;

#nullable disable

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261009130000_AddTaskInterfaceTypeToFeatureModules")]
public partial class AddTaskInterfaceTypeToFeatureModules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "task_interface_type",
            table: "feature_modules",
            type: "varchar(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "task_interface_type", table: "feature_modules");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        // Migration operations are explicit above; the current model snapshot is authoritative.
    }
}
