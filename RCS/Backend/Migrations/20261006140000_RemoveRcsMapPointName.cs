using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261006140000_RemoveRcsMapPointName")]
public sealed class RemoveRcsMapPointName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "point_name",
        table: "rcs_map_points");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "point_name",
        table: "rcs_map_points",
        type: "longtext",
        nullable: false,
        defaultValue: "");
}
