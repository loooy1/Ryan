using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Backend.Shared.Infrastructure;

#nullable disable

namespace RCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261006120000_AddRcsStationBusinessRules")]
public sealed class AddRcsStationBusinessRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "rcs_station_business_rules", columns: table => new
        {
            id = table.Column<long>(type: "bigint", nullable: false).Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
            map_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
            point_code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
            name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
            @event = table.Column<string>(name: "event", type: "varchar(32)", maxLength: 32, nullable: false),
            action_type = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
            execution_mode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
            http_method = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
            url = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false),
            headers_json = table.Column<string>(type: "json", nullable: false),
            request_body_template = table.Column<string>(type: "json", nullable: false),
            permit_response_path = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
            deny_message_path = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
            timeout_ms = table.Column<int>(type: "int", nullable: false), retry_count = table.Column<int>(type: "int", nullable: false),
            retry_delay_ms = table.Column<int>(type: "int", nullable: false), sort_order = table.Column<int>(type: "int", nullable: false),
            is_enabled = table.Column<bool>(type: "tinyint(1)", nullable: false), created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
            updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_rcs_station_business_rules", x => x.id));
        migrationBuilder.CreateIndex(name: "IX_rcs_station_business_rules_map_code_point_code_event_sort_order", table: "rcs_station_business_rules", columns: new[] { "map_code", "point_code", "event", "sort_order" });
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "rcs_station_business_rules");
}
