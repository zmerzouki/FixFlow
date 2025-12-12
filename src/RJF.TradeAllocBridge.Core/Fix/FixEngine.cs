using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Store;
using QuickFix.Transport;
using RJF.TradeAllocBridge.Core.Config;
using System.Reflection;

namespace RJF.TradeAllocBridge.Core.Fix;

public class FixEngine : IDisposable
{
    private readonly IInitiator _initiator;
    private readonly ILogger<FixEngine> _logger;
    private readonly FixConfig _config;
    private readonly string _configFile;
    private readonly SessionSettings _sessionSettings;

    public SessionSettings SessionSettings => _sessionSettings;

    public FixEngine(FixApp app, FixConfig config, ILogger<FixEngine> logger)
    {
        _logger = logger;
        _config = config;

        var configDir = Path.Combine(AppContext.BaseDirectory, "cfg");
        Directory.CreateDirectory(configDir);
        _configFile = Path.Combine(configDir, "FIX42.cfg");

        // Auto-generate configuration if missing
        if (!File.Exists(_configFile))
        {
            GenerateFix42Config(_config, _configFile);
            _logger.LogInformation("Generated FIX configuration file at {Path}", _configFile);
        }

        _sessionSettings = new SessionSettings(_configFile);
        var storeFactory = new FileStoreFactory(_sessionSettings);
        var logFactory = new ScreenLogFactory(_sessionSettings);
        var messageFactory = new DefaultMessageFactory();

        // Optional SSL initiator (for QuickFIX/n.SSL)
        Type? sslType = Type.GetType("QuickFix.SSLSocketInitiator, QuickFixn.SSL")
                          ?? Assembly.GetExecutingAssembly().GetType("QuickFix.SSLSocketInitiator");

        if (_config.UseSsl && sslType is not null)
        {
            try
            {
                _logger.LogInformation("Detected SSL-capable QuickFIX/n build, initializing secure initiator...");
                _initiator = (IInitiator)Activator.CreateInstance(
                    sslType,
                    app, storeFactory, _sessionSettings, logFactory, messageFactory
                )!;
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSL initialization failed, falling back to TCP initiator.");
            }
        }

        _logger.LogInformation("Initializing FIX engine over TCP {Host}:{Port}", _config.Host, _config.Port);
        _initiator = new SocketInitiator(app, storeFactory, _sessionSettings, logFactory, messageFactory);
    }

    // ✅ Generates a FIX 4.2-compatible config using a single DataDictionary
    private static void GenerateFix42Config(FixConfig cfg, string filePath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[DEFAULT]");
        sb.AppendLine("ConnectionType=initiator");
        sb.AppendLine($"ReconnectInterval={cfg.ReconnectInterval}");
        sb.AppendLine($"FileStorePath={cfg.FileStorePath}");
        sb.AppendLine($"StartTime={cfg.StartTime}");
        sb.AppendLine($"EndTime={cfg.EndTime}");
        sb.AppendLine($"HeartBtInt={cfg.HeartBtInt}");
        sb.AppendLine($"SocketConnectHost={cfg.Host}");
        sb.AppendLine($"SocketConnectPort={cfg.Port}");
        sb.AppendLine($"ResetOnLogout={(cfg.ResetOnLogout ? "Y" : "N")}");
        sb.AppendLine($"ResetOnDisconnect={(cfg.ResetOnDisconnect ? "Y" : "N")}");
        sb.AppendLine($"ValidateFieldsOutOfOrder={(cfg.ValidateFieldsOutOfOrder ? "Y" : "N")}");
        sb.AppendLine($"UseLocalTime={(cfg.UseLocalTime ? "Y" : "N")}");
        sb.AppendLine($"CheckLatency={(cfg.CheckLatency ? "Y" : "N")}");
        sb.AppendLine($"ContinueInitializationOnError={(cfg.ContinueInitializationOnError ? "Y" : "N")}");
        sb.AppendLine($"PersistMessages={(cfg.PersistMessages ? "Y" : "N")}");
        sb.AppendLine("UseDataDictionary=Y");

        // FIX 4.2 only uses one dictionary
        var dictPath = Path.Combine("cfg", "FIX42.xml").Replace("\\", "/");
        sb.AppendLine($"DataDictionary={dictPath}");

        sb.AppendLine();
        sb.AppendLine("[SESSION]");
        sb.AppendLine($"BeginString={cfg.BeginString}");
        sb.AppendLine($"SenderCompID={cfg.SenderCompId}");
        sb.AppendLine($"TargetCompID={cfg.TargetCompId}");

        File.WriteAllText(filePath, sb.ToString());
    }

    public void Start()
    {
        try
        {
            _logger.LogInformation("Starting FIX engine...");

            var dictPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");
            _logger.LogInformation("🔍 Checking FIX dictionary: {Path} (Exists={Exists})", dictPath, File.Exists(dictPath));

            if (!File.Exists(dictPath))
                throw new FileNotFoundException("FIX42.xml not found", dictPath);

            var bytes = File.ReadAllBytes(dictPath).Take(16).ToArray();
            _logger.LogInformation("  • First 16 bytes (hex): {Hex}", BitConverter.ToString(bytes));

            _initiator.Start();

            // Log session dictionary states
            foreach (SessionID sessionId in _initiator.GetSessionIDs())
            {
                var session = Session.LookupSession(sessionId);
                if (session == null)
                {
                    _logger.LogWarning("⚠️ Could not find session for {SessionID}", sessionId);
                    continue;
                }

                var ddProvider = session.DataDictionaryProvider;
                string ddDesc = "(none)";
                if (ddProvider != null)
                {
                    try
                    {
                        var dd = ddProvider.GetApplicationDataDictionary(sessionId.BeginString);
                        if (dd != null)
                            ddDesc = $"Loaded FIX dictionary with {dd.FieldsByTag.Count} fields, {dd.Messages.Count} messages";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to inspect DataDictionary for {SessionID}", sessionId);
                    }
                }

                _logger.LogInformation("🔍 Session {SessionID} dictionary info:", sessionId);
                _logger.LogInformation("   • DataDictionary: {Info}", ddDesc);
            }

            _logger.LogInformation("✅ FIX engine started successfully using {Config}", _configFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to start FIX engine.");
            throw;
        }
    }

    public void Stop()
    {
        _logger.LogInformation("Stopping FIX engine...");
        try
        {
            _initiator.Stop();
            _logger.LogInformation("FIX engine stopped cleanly.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during FIX engine shutdown.");
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
