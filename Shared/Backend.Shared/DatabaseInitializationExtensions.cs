using Backend.Shared.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Backend.Shared;

public static class DatabaseInitializationExtensions
{
    public static async Task InitializeSharedDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GrcsDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        if (db.Database.GetMigrations().Any())
            await db.Database.MigrateAsync();
        else
            await db.Database.EnsureCreatedAsync();

        app.Logger.LogInformation("数据库初始化完成，Provider={Provider} Database={Database}",
            db.Database.ProviderName, db.Database.GetDbConnection().Database);
    }
}
