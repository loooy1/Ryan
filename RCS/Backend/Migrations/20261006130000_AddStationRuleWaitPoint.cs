using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261006130000_AddStationRuleWaitPoint")]
public sealed class AddStationRuleWaitPoint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "wait_point_code", table: "rcs_station_business_rules", type: "varchar(128)", maxLength: 128,
        nullable: false, defaultValue: "");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "wait_point_code", table: "rcs_station_business_rules");
}
