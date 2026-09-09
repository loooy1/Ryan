using Backend.Shared;
using WCSBackend.Modules.Wcs;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── 本项目定位 ──
// WCS 后端（管理面）：对 GRCS 核心系统暴露 WCS 协议回调接口（/api/v1/*），
// 对 WCS 前端提供控制台查询/管理接口（/api/wcs/*）。监听端口由 appsettings.json 的
// Urls 决定（默认 http://0.0.0.0:8230）。GRCS 核心后端（8224）是另一个系统，不在本仓库。
//
// ── 模块化约定 ──
// 每个业务域一个 Modules/<域>/ 目录（Controllers/Models/Services），
// 通过 Modules/<域>/XxxModuleExtensions.AddXxxModule() 在此挂接注册。
// 共享设施（DbContext/仓储/WAL/管线）来自 Backend.Shared 类库。

// 控制器 + NewtonsoftJson：GRCS 按 "yyyy-MM-dd HH:mm:ss.fff" 反序列化响应中的 MsgTime，
// 序列化端必须用同一格式，否则 GRCS 解析失败会把外围作业置为异常。
builder.Services.AddGrcsJson();

// CORS：允许模拟器（浏览器 WASM）调试时直接访问本服务（生产环境用 CORS_ORIGIN 收紧）。
builder.Services.AddGrcsCors();

// SignalR：任务阶段事件实时推送（前端不再轮询 task-stages）
builder.Services.AddSignalR();

// 共享基础设施（SQLite WCS.db + 仓储基座 + WCS 实体配置程序集；迁移在 WCSBackend 程序集）
builder.Services.AddSharedModule("WCS.db", typeof(Program).Assembly, typeof(WcsModuleExtensions).Assembly);

// 模块注册（Wcs 总模块含 Automation/Proxy/Console/Realtime 子模块）
builder.Services.AddWcsModule();

var app = builder.Build();

// 全局异常兜底：未捕获异常统一返回 {"error":"..."}，避免堆栈泄露 + 前端 FriendlyError 可解析
app.UseGlobalErrorHandler();

// 启动时应用未执行的 EF 迁移（wcs_inventory 等新表自动建表）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<Backend.Shared.Infrastructure.GrcsDbContext>>().CreateDbContext();
    db.Database.Migrate();
    db.Dispose();
}

// SQLite WAL mode: readers and writers do not block each other, write conflicts queue (DefaultTimeout=30),
// fixes "database is locked" from concurrent writes (ledger init vs pick/dispatch).
SqliteWal.EnsureWal(Path.Combine(app.Environment.ContentRootPath, "WCS.db"));

app.UseCors();
app.MapControllers();
app.MapHub<WCSBackend.Modules.Wcs.Realtime.TaskStageRealtimeHub>("/hubs/task-stages");

// 健康检查：/health/ready（SQLite 连通性）
app.MapGrcsHealthCheck();

app.Run();