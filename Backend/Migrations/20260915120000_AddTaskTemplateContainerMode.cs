using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

public partial class AddTaskTemplateContainerMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "container_mode",
            table: "task_templates",
            type: "TEXT",
            nullable: false,
            defaultValue: "existing_inventory");

        migrationBuilder.Sql("UPDATE task_templates SET container_mode = 'generate_cargo' WHERE value = 'CARGO_CARRY_INBOUND'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "container_mode", table: "task_templates");
}
