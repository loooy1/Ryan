using GrcsBackend.Modules.Wcs;
using GrcsBackend.Modules.Shared;
using Microsoft.EntityFrameworkCore;
using GrcsBackend.Modules.Shared.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ── 本项目定位 ──
// WCS 后端（管理面）：对 GRCS 核心系统暴露 WCS 协议回调接口（/api/v1/*），
// 对 WCS 前端提供控制台查询/管理接口（/api/wcs/*）。监听端口由 appsettings.json 的
// Urls 决定（默认 http://0.0.0.0:8230）。GRCS 核心后端（8224）是另一个系统，不在本仓库。
//
// ── 模块化约定 ──
// 每个业务域一个 Modules/<域>/ 目录（Controllers/Models/Services），
// 通过 Modules/<域>/XxxModuleExtensions.AddXxxModule() 在此挂接注册。

// 控制器 + NewtonsoftJson：GRCS 按 "yyyy-MM-dd HH:mm:ss.fff" 反序列化响应中的 MsgTime，
// 序列化端必须用同一格式，否则 GRCS 解析失败会把外围作业置为异常。
builder.Services.AddControllers().AddNewtonsoftJson(options =>
{
    options.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss.fff";
});

// CORS：允许模拟器（浏览器 WASM）调试时直接访问本服务。
// 注意：SignalR JS 客户端默认带凭证（withCredentials=true），浏览器禁止凭证请求匹配
// AllowAnyOrigin() 的 `*`，否则 negotiate 被拦截（Failed to fetch）。
// 必须回显具体来源 SetIsOriginAllowed(_ => true) + AllowCredentials()。
// 生产环境应收紧为前端实际域名：.SetIsOriginAllowed(h => h == "https://front.example.com")。
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// SignalR：任务阶段事件实时推送（前端不再轮询 task-stages）
builder.Services.AddSignalR();

// 模块注册（Shared 共享基础设施 → Wcs 总模块含 Automation/Proxy/Console/Realtime 子模块）
builder.Services.AddSharedModule();
builder.Services.AddWcsModule();

var app = builder.Build();

// 启动时应用未执行的 EF 迁移（wcs_inventory 等新表自动建表）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GrcsBackend.Modules.Shared.Infrastructure.GrcsDbContext>>().CreateDbContext();
    db.Database.Migrate();
    db.Dispose();
}

app.UseCors();
app.MapControllers();
app.MapHub<GrcsBackend.Modules.Wcs.Realtime.TaskStageRealtimeHub>("/hubs/task-stages");

app.Run();
