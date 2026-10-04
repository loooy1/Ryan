using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRcsVehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "requested_vehicle_id",
                table: "rcs_tasks",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "rcs_vehicles",
                columns: table => new
                {
                    vehicle_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    protocol = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    initial_point_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rcs_vehicles", x => x.vehicle_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
            // 保留原有 V-01；仅首次创建车辆表时添加，不在后续启动时重建已删除车辆。
            migrationBuilder.InsertData(
                table: "rcs_vehicles",
                columns: new[] { "vehicle_id", "name", "protocol", "initial_point_code", "is_enabled", "created_at", "updated_at" },
                values: new object[] { "V-01", "虚拟车 01", "VIRTUAL", "", true,
                    new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "rcs_vehicles");
            migrationBuilder.DropColumn(name: "requested_vehicle_id", table: "rcs_tasks");
        }
    }
}
