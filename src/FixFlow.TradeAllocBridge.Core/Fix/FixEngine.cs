using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Store;
using QuickFix.Transport;
using FixFlow.TradeAllocBridge.Core.Config;
using System.Reflection;
using System.Text;

namespace FixFlow.TradeAllocBridge.Core.Fix
{
    public class FixEngine : IDisposable
    {
        private IInitiator? _initiator;
        private readonly ILogger<FixEngine> _logger;
        private readonly FixConfig _config;
        private readonly string _configFile;
        private readonly string _dictPath;
        private SessionSettings? _sessionSettings;      

        public SessionSettings? SessionSettings => _sessionSettings;

        public FixEngine(FixApp app, FixConfig config, ILogger<FixEngine> logger)
        {
            _logger = logger;
            _config = config;

            var configDir = Path.Combine(AppContext.BaseDirectory, "cfg");
            Directory.CreateDirectory(configDir);
            _configFile = Path.Combine(configDir, "FIX42.cfg");
            _dictPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");

            if (!File.Exists(_configFile))
            {
                WriteDefaultFixConfig();
                _logger.LogInformation("✅ FIX configuration file generated at {Path}", _configFile);
            }
            else
            {
                _logger.LogInformation("✅ Using existing FIX configuration file at {Path}", _configFile);
            }
        }

        // --------------------------------------------------------------
        // Create base [DEFAULT] config once
        // --------------------------------------------------------------
        private void WriteDefaultFixConfig()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[DEFAULT]");
            sb.AppendLine("ConnectionType=initiator");
            sb.AppendLine("ReconnectInterval=60");
            sb.AppendLine("StartTime=00:00:00");
            sb.AppendLine("EndTime=23:59:59");
            sb.AppendLine("HeartBtInt=30");
            sb.AppendLine("SocketConnectHost=127.0.0.1");
            sb.AppendLine("SocketConnectPort=5001");
            sb.AppendLine("ResetOnLogout=Y");
            sb.AppendLine("ResetOnLogon=Y");
            sb.AppendLine("ResetOnDisconnect=Y");
            sb.AppendLine("ValidateUserDefinedFields=N");
            sb.AppendLine("ValidateFieldsOutOfOrder=N");
            sb.AppendLine("ValidateFieldsHaveValues=N");
            sb.AppendLine("UseLocalTime=Y");
            sb.AppendLine("CheckLatency=N");
            sb.AppendLine("ContinueInitializationOnError=Y");
            sb.AppendLine("PersistMessages=Y");
            sb.AppendLine("UseDataDictionary=Y");
            sb.AppendLine($"DataDictionary={_dictPath}");
            sb.AppendLine();
            File.WriteAllText(_configFile, sb.ToString());
        }

        // --------------------------------------------------------------
        // Append new sessions instead of overwriting the file
        // --------------------------------------------------------------
        public void AppendSessionsIfMissing(IEnumerable<(string Sender, string Target)> sessions)
        {
            if (!File.Exists(_configFile))
                WriteDefaultFixConfig();

            var existing = File.ReadAllText(_configFile);
            var sb = new StringBuilder();

            foreach (var (Sender, Target) in sessions)
            {
                string marker = $"SenderCompID={Sender}";
                if (existing.Contains(marker))
                {
                    _logger.LogInformation("ℹ️ Session {Sender}->{Target} already exists, skipping append.", Sender, Target);
                    continue;
                }

                _logger.LogInformation("➕ Appending new session {Sender}->{Target} to FIX42.cfg", Sender, Target);

                sb.AppendLine("[SESSION]");
                sb.AppendLine("BeginString=FIX.4.2");
                sb.AppendLine("FileStorePath=store");
                sb.AppendLine($"SenderCompID={Sender}");
                sb.AppendLine($"TargetCompID={Target}");
                sb.AppendLine();
            }

            if (sb.Length > 0)
                File.AppendAllText(_configFile, sb.ToString());
        }

        // --------------------------------------------------------------
        // Reload configuration and initialize initiator
        // --------------------------------------------------------------
        public void ReloadSettings(FixApp app)
        {
            _logger.LogInformation("♻️ Reloading SessionSettings and reinitializing initiator...");

            _sessionSettings = new SessionSettings(_configFile);
            foreach (SessionID sessionId in _sessionSettings.GetSessions())
            {
                var dict = _sessionSettings.Get(sessionId);
                dict.SetString("DataDictionary", _dictPath);
            }

            var storeFactory = new FileStoreFactory(_sessionSettings);
            var logFactory = new ScreenLogFactory(_sessionSettings);
            var messageFactory = new DefaultMessageFactory();
            _initiator = new SocketInitiator(app, storeFactory, _sessionSettings, logFactory, messageFactory);
        }

        // --------------------------------------------------------------
        // Start the FIX initiator
        // --------------------------------------------------------------
        public void Start()
        {
            if (_initiator == null)
            {
                _logger.LogError("❌ Initiator not initialized. Did you call ReloadSettings()?");
                return;
            }

            _logger.LogInformation("Starting FIX engine...");

            try
            {
                var dd = new QuickFix.DataDictionary.DataDictionary(_dictPath);
                _logger.LogInformation("✅ FIX42 dictionary parsed successfully (Fields={Fields}, Messages={Msgs})",
                    dd.FieldsByTag.Count, dd.Messages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load FIX42.xml dictionary.");
            }

            _initiator.Start();

            foreach (SessionID sessionId in _initiator.GetSessionIDs())
            {
                var session = Session.LookupSession(sessionId);
                if (session == null)
                {
                    _logger.LogWarning("⚠️ Session {SessionID} not found after start", sessionId);
                    continue;
                }

                var ddProvider = session.DataDictionaryProvider;
                string ddDesc = "(none)";
                if (ddProvider != null)
                {
                    var dd = ddProvider.GetSessionDataDictionary(sessionId.BeginString);
                    if (dd != null)
                        ddDesc = $"Loaded FIX dictionary with {dd.FieldsByTag.Count} fields, {dd.Messages.Count} messages";
                }

                _logger.LogInformation("🔍 Session {SessionID} dictionary info:", sessionId);
                _logger.LogInformation("   • DataDictionary: {Info}", ddDesc);
            }

            _logger.LogInformation("✅ FIX engine started successfully using {Config}", _configFile);
        }

        public void Stop()
        {
            try
            {
                _initiator?.Stop();
                _logger.LogInformation("🛑 FIX engine stopped cleanly.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping FIX engine.");
            }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
