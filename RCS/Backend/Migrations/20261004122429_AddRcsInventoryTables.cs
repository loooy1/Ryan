using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RCSBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRcsInventoryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rcs_inventory_instances",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    item_type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    instance_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    model_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    map_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    point_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    parent_instance_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    offset_x_mm = table.Column<double>(type: "double", nullable: false),
                    offset_y_mm = table.Column<double>(type: "double", nullable: false),
                    offset_z_mm = table.Column<double>(type: "double", nullable: false),
                    metadata_json = table.Column<string>(type: "json", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rcs_inventory_instances", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "rcs_inventory_models",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    item_type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    model_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    length_mm = table.Column<double>(type: "double", nullable: false),
                    width_mm = table.Column<double>(type: "double", nullable: false),
                    height_mm = table.Column<double>(type: "double", nullable: false),
                    weight_kg = table.Column<double>(type: "double", nullable: false),
                    max_load_kg = table.Column<double>(type: "double", nullable: true),
                    is_enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    metadata_json = table.Column<string>(type: "json", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rcs_inventory_models", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_rcs_inventory_instances_item_type_instance_code",
                table: "rcs_inventory_instances",
                columns: new[] { "item_type", "instance_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rcs_inventory_instances_map_code_point_code",
                table: "rcs_inventory_instances",
                columns: new[] { "map_code", "point_code" });

            migrationBuilder.CreateIndex(
                name: "IX_rcs_inventory_instances_parent_instance_code",
                table: "rcs_inventory_instances",
                column: "parent_instance_code");

            migrationBuilder.CreateIndex(
                name: "IX_rcs_inventory_models_item_type_model_code",
                table: "rcs_inventory_models",
                columns: new[] { "item_type", "model_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rcs_inventory_instances");

            migrationBuilder.DropTable(
                name: "rcs_inventory_models");
        }
    }
}
