using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261020180000_RemoveRcsTemplateBaseUrl")]
public sealed class RemoveRcsTemplateBaseUrl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "base_url", table: "feature_modules");

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "base_url",
            table: "feature_modules",
            type: "varchar(2048)",
            maxLength: 2048,
            nullable: false,
            defaultValue: "");
}
