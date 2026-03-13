using System;
using System.IO;

namespace FixFlow.TradeAllocBridge.Core.Config;

public static class SharedConfigResolver
{
    public const string SharedAppSettingsEnvVar = "FIXFLOW_SHARED_APPSETTINGS";

    public static string? ResolveSharedAppSettingsPath(string? baseDirectory = null)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable(SharedAppSettingsEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                var envPath = Path.GetFullPath(env);
                if (File.Exists(envPath))
                {
                    return envPath;
                }
            }
        }
        catch
        {
            // ignore env resolution errors
        }

        var baseDir = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        var candidates = new[]
        {
            Path.Combine(baseDir, "Shared", "appsettings.json"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "Shared", "appsettings.json"))
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore invalid path issues
            }
        }

        return null;
    }
}
