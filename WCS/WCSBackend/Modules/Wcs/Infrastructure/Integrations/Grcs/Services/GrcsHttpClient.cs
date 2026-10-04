using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Proxy.Services;

/// <summary>
/// GRCS 核心后端（8224）HTTP 客户端：任务下发 / 车辆任务 / 库存查询 / 出站信号。
/// baseUrl 每调用传入（来自 WcsSettingsService，前端连接设置可改）。
/// </summary>
public class GrcsHttpClient
{
    private readonly IHttpClientFactory _factory;
    private readonly object _healthLock = new();
    private bool? _grcsOnline;
    private DateTime _healthCheckedAt = DateTime.MinValue;

    public GrcsHttpClient(IHttpClientFactory factory) => _factory = factory;

    /// <summary>GRCS 在线状态缓存（PingAsync 每次探测写入，5 秒内有效；超期返回 null）。
    /// 供自动发送类服务（信号自动放行等）熔断：离线时不发起真实请求。</summary>
    public bool? GrcsOnline
    {
        get
        {
            lock (_healthLock)
            {
                return (DateTime.Now - _healthCheckedAt).TotalSeconds < 5 ? _grcsOnline : null;
            }
        }
    }

    private HttpClient NewClient()
    {
        var c = _factory.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(10);
        return c;
    }

    /// <summary>任务组下发（/api/v1/task_receive）。</summary>
    public Task<(bool Ok, int StatusCode, string Json)> SendTaskGroupAsync(string baseUrl, WcsTaskGroup payload)
    {
        payload.MsgTime = NormalizeProtocolTime(payload.MsgTime);
        return PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/task_receive", payload);
    }

    /// <summary>车辆任务（/api/RawOrder/ChangeFloor，MOVE_ONLY 纯移动）。
    /// 超时 7 秒：必须早于前端 dispatch 限时（8 秒），保证超时时后端先落失败日志、前后端判定一致。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> SendVehicleOrderAsync(string baseUrl, VehicleOrderRequest payload)
    {
        payload.CreateTime = NormalizeProtocolTime(payload.CreateTime);
        try
        {
            var c = NewClient();
            c.Timeout = TimeSpan.FromSeconds(7);
            var resp = await c.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/RawOrder/ChangeFloor", payload);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>
    /// 统一 GRCS 协议时间：只保留本地时间的年月日、时分秒，不发送 ISO 时区和毫秒。
    /// 代理层再次规范化，兼容旧版前端仍提交带时区的时间。
    /// </summary>
    private static string NormalizeProtocolTime(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var offsetTime))
            return offsetTime.DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var localTime))
            return localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>查询全部车辆及当前状态（GET /api/Vehicle/GetAllVehicles，归巢模式用）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> QueryVehiclesAsync(string baseUrl, string scene)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/Vehicle/GetAllVehicles?sceneName={Uri.EscapeDataString(scene)}";
        try
        {
            var resp = await NewClient().GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>库存查询（/api/Cargo，支持编码/场景/锁定过滤 + 分页）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> QueryCargoInventoryAsync(string baseUrl, string scene,
        string? code = null, string? locked = null, int pageNo = 1, int pageSize = 2000)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/Cargo?pageNo={pageNo}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(code)) url += $"&SearchContextParams[Code]={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrWhiteSpace(scene)) url += $"&SearchContextParams[HomeStationScene]={Uri.EscapeDataString(scene)}";
        if (!string.IsNullOrWhiteSpace(locked)) url += $"&SearchContextParams[IsLocked]={locked}";
        try
        {
            var resp = await NewClient().GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>读取 RCS 配置的货物尺寸模型（GET /api/CargoSize）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> QueryCargoSizesAsync(string baseUrl)
    {
        try
        {
            var resp = await NewClient().GetAsync($"{baseUrl.TrimEnd('/')}/api/CargoSize");
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>模拟生成容器入库（GET /AutoContainerEnter，场景名取设置）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> AutoContainerEnterAsync(string baseUrl, string sceneName,
        string prefix = "container", int num = -1, int floor = -1, int type = 1)
    {
        var url = $"{baseUrl.TrimEnd('/')}/AutoContainerEnter?sceneName={Uri.EscapeDataString(sceneName)}"
            + $"&prefix={Uri.EscapeDataString(prefix)}&num={num}&floor={floor}&type={type}";
        try
        {
            var resp = await NewClient().GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>指定站点创建 RCS 库存（POST /api/Cargo/Enter）。</summary>
    public Task<(bool Ok, int StatusCode, string Json)> EnterCargoAsync<T>(string baseUrl, T payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/Cargo/Enter", payload);

    /// <summary>地图 zip 下载（GET /api/Map/GetMap，场景名取设置）。</summary>
    public async Task<(bool Ok, int StatusCode, byte[] Bytes, string Error)> GetMapBytesAsync(string baseUrl, string sceneName)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/Map/GetMap?sceneName={Uri.EscapeDataString(sceneName)}&getTypes=feMap";
        try
        {
            var resp = await NewClient().GetAsync(url);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, bytes, "");
        }
        catch (Exception ex) { return (false, 0, Array.Empty<byte>(), ex.Message); }
    }

    // ── 出站信号（WCS → GRCS，信号自动放行用）──

    public Task<(bool Ok, int StatusCode, string Json)> SendContainerReadyAsync(string baseUrl, object payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/container_ready", payload);

    public Task<(bool Ok, int StatusCode, string Json)> SendContainerRemoveAsync(string baseUrl, object payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/container_remove", payload);

    public Task<(bool Ok, int StatusCode, string Json)> SendOperationFinishAsync(string baseUrl, object payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/container_operation_finish", payload);

    private async Task<(bool Ok, int StatusCode, string Json)> PostAsync<T>(string url, T payload)
    {
        try
        {
            var resp = await NewClient().PostAsJsonAsync(url, payload);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>存活探测：测试配置地址的 TCP 端口是否有程序监听（2 秒超时）。
    /// 不依赖 GRCS 是否提供 HTTP 首页或健康接口；只要目标端口接受 TCP 连接即可视为服务可达。
    /// 探测结果写入 GrcsOnline 缓存（自动发送类服务熔断依据）。</summary>
    public async Task<bool> PingAsync(string baseUrl)
    {
        try
        {
            if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                lock (_healthLock) { _grcsOnline = false; _healthCheckedAt = DateTime.Now; }
                return false;
            }

            var port = uri.Port > 0
                ? uri.Port
                : uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;

            using var socket = new TcpClient();
            await socket.ConnectAsync(uri.Host, port).WaitAsync(TimeSpan.FromSeconds(2));
            var online = socket.Connected;
            lock (_healthLock) { _grcsOnline = online; _healthCheckedAt = DateTime.Now; }
            return online;
        }
        catch
        {
            lock (_healthLock) { _grcsOnline = false; _healthCheckedAt = DateTime.Now; }
            return false;
        }
    }

    /// <summary>通用转发：向任意 GRCS URL 发送原始 JSON 报文（功能模块/信号通用下发接口使用）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> ForwardAsync(string url, HttpMethod method, string? rawJson)
    {
        try
        {
            var client = NewClient();
            HttpResponseMessage resp;
            if (method == HttpMethod.Get)
            {
                resp = await client.GetAsync(url);
            }
            else
            {
                var content = new StringContent(rawJson ?? "{}", Encoding.UTF8, "application/json");
                if (method == HttpMethod.Put) resp = await client.PutAsync(url, content);
                else if (method == HttpMethod.Delete) resp = await client.DeleteAsync(url);
                else resp = await client.PostAsync(url, content);
            }
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }
}
