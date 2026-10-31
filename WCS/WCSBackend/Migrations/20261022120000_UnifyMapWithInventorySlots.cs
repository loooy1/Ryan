using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WCSBackend.Migrations;

[DbContext(typeof(GrcsDbContext))]
[Migration("20261022120000_UnifyMapWithInventorySlots")]
public sealed class UnifyMapWithInventorySlots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "station_type_bits", table: "wcs_slots", type: "int", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<bool>(name: "sta_enable", table: "wcs_slots", type: "tinyint(1)", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<string>(name: "cargo_areas_json", table: "wcs_slots", type: "longtext", nullable: true);
        migrationBuilder.AddColumn<int>(name: "support_load_state", table: "wcs_slots", type: "int", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<bool>(name: "allow_turn", table: "wcs_slots", type: "tinyint(1)", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<bool>(name: "allow_avoid", table: "wcs_slots", type: "tinyint(1)", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<bool>(name: "allow_stop", table: "wcs_slots", type: "tinyint(1)", nullable: false, defaultValue: true);

        // Seed any map stations that were never written to the inventory ledger.
        migrationBuilder.Sql("""
            INSERT IGNORE INTO `wcs_slots` (
                `mark`, `site_type`, `station_type_bits`, `floor`, `x`, `y`, `sta_enable`, `cargo_areas_json`,
                `support_load_state`, `allow_turn`, `allow_avoid`, `allow_stop`, `pallet_code`, `pallet_status`,
                `cargo_code`, `cargo_status`, `task_lock_id`, `parent_station_code`, `selection_status`, `updated_at`
            )
            SELECT m.`mark`,
                CASE
                    WHEN (m.`station_type_bits` & 4) <> 0 THEN 'Storage'
                    WHEN (m.`station_type_bits` & 8) <> 0 THEN 'Terminal'
                    WHEN (m.`station_type_bits` & 128) <> 0 THEN 'PeopleStation'
                    ELSE 'Sorting'
                END,
                m.`station_type_bits`, m.`floor`, m.`x`, m.`y`, COALESCE(m.`sta_enable`, 1),
                COALESCE(JSON_UNQUOTE(JSON_EXTRACT(k.`value`, CONCAT('$.Stations[', m.`station_index` - 1, '].CargoAreas'))), '[]'),
                COALESCE(m.`support_load_state`, 0), COALESCE(m.`allow_turn`, 1),
                COALESCE(m.`allow_avoid`, 1), COALESCE(m.`allow_stop`, 1),
                '', '', '', '', '', '',
                CASE WHEN (m.`station_type_bits` & 4) <> 0 THEN 'start_unavailable' ELSE 'available' END,
                ''
            FROM `kv` AS k
            JOIN JSON_TABLE(IF(JSON_VALID(k.`value`), k.`value`, '{}'), '$.Stations[*]' COLUMNS (
                `station_index` FOR ORDINALITY,
                `mark` VARCHAR(255) PATH '$.Mark',
                `station_type_bits` INT PATH '$.StationType',
                `x` DOUBLE PATH '$.X',
                `y` DOUBLE PATH '$.Y',
                `floor` INT PATH '$.Floor',
                `sta_enable` TINYINT PATH '$.StaEnable',
                `support_load_state` INT PATH '$.SupportLoadState',
                `allow_turn` TINYINT PATH '$.AllowTurn',
                `allow_avoid` TINYINT PATH '$.AllowAvoid',
                `allow_stop` TINYINT PATH '$.AllowStop'
            )) AS m
            WHERE k.`key` = 'map_stations'
                AND (m.`station_type_bits` & (4 | 8 | 64 | 128)) <> 0
                AND NOT EXISTS (SELECT 1 FROM `wcs_slots` AS existing WHERE existing.`mark` = m.`mark`)
            """);

        // Copy existing map-only attributes into the inventory rows before deleting the legacy kv value.
        migrationBuilder.Sql("""
            UPDATE `wcs_slots` AS s
            JOIN `kv` AS k ON k.`key` = 'map_stations'
            JOIN JSON_TABLE(IF(JSON_VALID(k.`value`), k.`value`, '{}'), '$.Stations[*]' COLUMNS (
                `station_index` FOR ORDINALITY,
                `mark` VARCHAR(255) PATH '$.Mark',
                `station_type_bits` INT PATH '$.StationType',
                `sta_enable` TINYINT PATH '$.StaEnable',
                `support_load_state` INT PATH '$.SupportLoadState',
                `allow_turn` TINYINT PATH '$.AllowTurn',
                `allow_avoid` TINYINT PATH '$.AllowAvoid',
                `allow_stop` TINYINT PATH '$.AllowStop'
            )) AS m ON m.`mark` = s.`mark`
            SET s.`station_type_bits` = COALESCE(m.`station_type_bits`, 0),
                s.`sta_enable` = COALESCE(m.`sta_enable`, 1),
                s.`cargo_areas_json` = COALESCE(JSON_UNQUOTE(JSON_EXTRACT(k.`value`,
                    CONCAT('$.Stations[', m.`station_index` - 1, '].CargoAreas'))), '[]'),
                s.`support_load_state` = COALESCE(m.`support_load_state`, 0),
                s.`allow_turn` = COALESCE(m.`allow_turn`, 1),
                s.`allow_avoid` = COALESCE(m.`allow_avoid`, 1),
                s.`allow_stop` = COALESCE(m.`allow_stop`, 1)
            """);

        migrationBuilder.Sql("UPDATE `wcs_slots` SET `cargo_areas_json` = '[]' WHERE `cargo_areas_json` IS NULL");
        migrationBuilder.AlterColumn<string>(name: "cargo_areas_json", table: "wcs_slots", type: "longtext", nullable: false,
            oldClrType: typeof(string), oldType: "longtext", oldNullable: true);

        migrationBuilder.Sql("DELETE FROM `kv` WHERE `key` = 'map_stations'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO `kv` (`key`, `value`)
            SELECT 'map_stations', JSON_OBJECT(
                'SavedAt', COALESCE(MAX(s.`updated_at`), ''),
                'PathsCount', 0,
                'Stations', COALESCE(JSON_ARRAYAGG(JSON_OBJECT(
                    'Mark', s.`mark`,
                    'StationType', s.`station_type_bits`,
                    'X', s.`x`, 'Y', s.`y`, 'Floor', s.`floor`,
                    'StaEnable', s.`sta_enable`,
                    'CargoAreas', CAST(s.`cargo_areas_json` AS JSON),
                    'SupportLoadState', s.`support_load_state`,
                    'AllowTurn', s.`allow_turn`, 'AllowAvoid', s.`allow_avoid`, 'AllowStop', s.`allow_stop`
                )), JSON_ARRAY())
            )
            FROM `wcs_slots` AS s
            ON DUPLICATE KEY UPDATE `value` = VALUES(`value`)
            """);

        migrationBuilder.DropColumn(name: "station_type_bits", table: "wcs_slots");
        migrationBuilder.DropColumn(name: "sta_enable", table: "wcs_slots");
        migrationBuilder.DropColumn(name: "cargo_areas_json", table: "wcs_slots");
        migrationBuilder.DropColumn(name: "support_load_state", table: "wcs_slots");
        migrationBuilder.DropColumn(name: "allow_turn", table: "wcs_slots");
        migrationBuilder.DropColumn(name: "allow_avoid", table: "wcs_slots");
        migrationBuilder.DropColumn(name: "allow_stop", table: "wcs_slots");
    }
}
