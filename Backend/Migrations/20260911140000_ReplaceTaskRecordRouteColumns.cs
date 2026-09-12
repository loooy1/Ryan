using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

/// <summary>任务路线由 JSON 路由和单一当前站点改为起点与终点两个持久字段。</summary>
public partial class ReplaceTaskRecordRouteColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite 的 EF 驱动不支持 DropColumn。重建表时同时把旧 JSON 路线复制到每个阶段行，
        // 使历史任务也能按起点、终点查询完整生命周期。
        migrationBuilder.Sql("""
            CREATE TABLE "__task_records_new" (
                "id" INTEGER NOT NULL CONSTRAINT "PK_task_records" PRIMARY KEY AUTOINCREMENT,
                "task_id" TEXT NOT NULL,
                "stage" TEXT NOT NULL,
                "time" TEXT NOT NULL,
                "warehouse" TEXT NOT NULL,
                "container_code" TEXT NOT NULL,
                "cargo_code" TEXT NOT NULL,
                "task_type" TEXT NOT NULL,
                "start_station_code" TEXT NOT NULL,
                "end_station_code" TEXT NOT NULL,
                "ok" INTEGER NOT NULL,
                "status_code" INTEGER NOT NULL
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "__task_records_new" (
                "id", "task_id", "stage", "time", "warehouse", "container_code", "cargo_code", "task_type",
                "start_station_code", "end_station_code", "ok", "status_code")
            SELECT
                source."id", source."task_id", source."stage", source."time", source."warehouse",
                source."container_code", source."cargo_code", source."task_type",
                COALESCE(
                    NULLIF(json_extract(source."route_codes", '$[0]'), ''),
                    NULLIF((SELECT json_extract(created."route_codes", '$[0]')
                            FROM "task_records" AS created
                            WHERE created."task_id" = source."task_id" AND created."stage" = 'CREATED'
                            ORDER BY created."id" LIMIT 1), ''),
                    ''),
                COALESCE(
                    NULLIF(json_extract(source."route_codes", '$[1]'), ''),
                    NULLIF((SELECT json_extract(created."route_codes", '$[1]')
                            FROM "task_records" AS created
                            WHERE created."task_id" = source."task_id" AND created."stage" = 'CREATED'
                            ORDER BY created."id" LIMIT 1), ''),
                    ''),
                source."ok", source."status_code"
            FROM "task_records" AS source;
            """);
        migrationBuilder.Sql("""DROP TABLE "task_records";""");
        migrationBuilder.Sql("""ALTER TABLE "__task_records_new" RENAME TO "task_records";""");
        migrationBuilder.Sql("""CREATE INDEX "idx_tr_task" ON "task_records" ("task_id");""");
        migrationBuilder.Sql("""CREATE INDEX "idx_tr_stage" ON "task_records" ("stage");""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "__task_records_old" (
                "id" INTEGER NOT NULL CONSTRAINT "PK_task_records" PRIMARY KEY AUTOINCREMENT,
                "task_id" TEXT NOT NULL,
                "stage" TEXT NOT NULL,
                "time" TEXT NOT NULL,
                "warehouse" TEXT NOT NULL,
                "container_code" TEXT NOT NULL,
                "cargo_code" TEXT NOT NULL,
                "task_type" TEXT NOT NULL,
                "route_codes" TEXT NOT NULL,
                "station_code" TEXT NOT NULL,
                "ok" INTEGER NOT NULL,
                "status_code" INTEGER NOT NULL
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "__task_records_old" (
                "id", "task_id", "stage", "time", "warehouse", "container_code", "cargo_code", "task_type",
                "route_codes", "station_code", "ok", "status_code")
            SELECT
                "id", "task_id", "stage", "time", "warehouse", "container_code", "cargo_code", "task_type",
                CASE WHEN "start_station_code" = '' AND "end_station_code" = '' THEN '[]'
                     ELSE json_array("start_station_code", "end_station_code") END,
                '', "ok", "status_code"
            FROM "task_records";
            """);
        migrationBuilder.Sql("""DROP TABLE "task_records";""");
        migrationBuilder.Sql("""ALTER TABLE "__task_records_old" RENAME TO "task_records";""");
        migrationBuilder.Sql("""CREATE INDEX "idx_tr_task" ON "task_records" ("task_id");""");
        migrationBuilder.Sql("""CREATE INDEX "idx_tr_stage" ON "task_records" ("stage");""");
    }
}
