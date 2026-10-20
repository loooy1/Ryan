using WCSBackend.Modules.CommonTools.Services;
using Microsoft.AspNetCore.DataProtection;

namespace WCSBackend.Modules.CommonTools;

public static class CommonToolsModuleExtensions
{
    public static IServiceCollection AddCommonToolsModule(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName("WCSBackend.CommonTools");
        services.AddSingleton<CommonToolsDatabaseConfigStore>();
        services.AddSingleton<NestedCargoService>();
        return services;
    }
}
