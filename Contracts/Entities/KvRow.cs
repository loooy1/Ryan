namespace GrcsBackend.Contracts.Entities;

/// <summary>键值配置行（kv 表，map_stations / auto_range / wcs_settings / nest_config / cargo_codes / sig_* 等）。</summary>
public class KvRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}