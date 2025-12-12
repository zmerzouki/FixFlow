using Serilog;

namespace RJF.TradeAllocBridge.Core.Logging;

public static class LogService
{
    public static void Configure(Config.LoggingConfig config)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(config.Path, rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }
}
