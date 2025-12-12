using QuickFix;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Globalization;

namespace RJF.TradeAllocBridge.Core.Fix
{
    public class FixClient
    {
        private readonly ILogger<FixClient> _logger;
        private readonly FixApp _app;
        private readonly string _logPath;
        private readonly QuickFix.DataDictionary.DataDictionary? _fix42TransportDict;
        private readonly QuickFix.DataDictionary.DataDictionary? _fix42AppDict;

        public FixClient(ILogger<FixClient> logger, FixApp app)
        {
            _logger = logger;
            _app = app;

            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(_logPath);

            try
            {
                var baseDir = AppContext.BaseDirectory;
                var fixTransport = Path.Combine(baseDir, "cfg", "FIX42.xml");
                var fixApp = Path.Combine(baseDir, "cfg", "FIX42.xml");

                if (File.Exists(fixTransport))
                {
                    _fix42TransportDict = new QuickFix.DataDictionary.DataDictionary(fixTransport);
                    _fix42AppDict = new QuickFix.DataDictionary.DataDictionary(fixApp);
                    _logger.LogInformation("✅ FIX42 dictionaries loaded successfully for validation from {Path}", fixTransport);
                }
                else
                {
                    _logger.LogWarning("⚠️ FIX42.xml not found at {Path}. Structural validation will be skipped.", fixTransport);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load FIX42 DataDictionary for validation.");
            }
        }

        public async Task<string> SendAsync(Message msg)
        {
            try
            {
                var validationErrors = ValidateAllocation(msg);
                if (validationErrors.Any())
                {
                    var errorText = string.Join("; ", validationErrors);
                    _logger.LogError("FIX validation failed. Message not sent. Errors: {Errors}", errorText);
                    return errorText;
                }


                // ✅ Deep copy for logging
                var msgClone = CloneMessage(msg);
                await LogFixMessageAsync(msgClone, "OUTBOUND");

                
                // ✅ Send via active FIX session
                if (_app.ActiveSession != null)
                {
                    bool ok = Session.SendToTarget(msg, _app.ActiveSession.SessionID);
                    _logger.LogInformation("FIX message sent: {Status}", ok ? "OK" : "FAILED");
                    return ok ? "OK" : "FAILED";
                }
                if (_app.Engine != null && _app.Engine.SessionSettings != null && _app.ActiveSession != null)
                {
                    var sessionId = _app.ActiveSession.SessionID;
                    var section = _app.Engine.SessionSettings.Get(sessionId);

                    var appDict = section.Has("AppDataDictionary") ? section.GetString("AppDataDictionary") : "(none)";
                    var transportDict = section.Has("TransportDataDictionary") ? section.GetString("TransportDataDictionary") : "(none)";

                    _logger.LogDebug("🔍 Live session {Session} dictionaries: App={AppDict}, Transport={TransportDict}", sessionId, appDict, transportDict);
                }

                _logger.LogWarning("No active FIX session found (not logged on yet).");
                return "NoSession";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending FIX message.");
                return "Error";
            }
        }

        // ================================
        // FIX Message Validation
        // ================================
        private List<string> ValidateAllocation(Message msg)
        {
            var errors = new List<string>();

            try
            {
                // 🔍 Determine consistent FIX42.xml path (CLI or service runtime)
                var basePath = AppDomain.CurrentDomain.BaseDirectory;
                var dictPath = Path.Combine(basePath, "cfg", "FIX42.xml");

                if (!File.Exists(dictPath))
                {
                    // fallback: for cases when executing from Core instead of CLI
                    var alt = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "cfg", "FIX42.xml"));
                    if (File.Exists(alt))
                        dictPath = alt;
                }

                _logger.LogDebug("🔍 FIX dictionary base path: {Base}", basePath);
                _logger.LogDebug("🔍 FIX dictionary exists? {Exists}", File.Exists(dictPath));
                _logger.LogDebug("🔍 Using dictionary at: {Path}", dictPath);

                if (!File.Exists(dictPath))
                {
                    _logger.LogWarning("⚠️ FIX42 dictionary not found at {Path}. Skipping structural validation.", dictPath);
                    return errors;
                }

                var dd = new QuickFix.DataDictionary.DataDictionary(dictPath);
                var msgType = msg.Header.GetString(QuickFix.Fields.Tags.MsgType);

                _logger.LogDebug("Performing FIX42 structural validation for MsgType={MsgType}", msgType);
                QuickFix.DataDictionary.DataDictionary.Validate(msg, null, dd, "FIX.4.2", msgType);
            }
            catch (QuickFix.RequiredTagMissing ex)
            {
                _logger.LogWarning("⚠️ FIX42 structural validation failed. Required tag missing: {Message}", ex.Message);
                errors.Add("Structural validation error: Required tag missing");
            }
            catch (QuickFix.InvalidMessage ex)
            {
                _logger.LogWarning("⚠️ FIX message structure invalid: {Message}", ex.Message);
                errors.Add("Structural validation error: Invalid message structure");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ FIX structural validation failed unexpectedly: {Message}", ex.Message);
                errors.Add($"Structural validation error: {ex.Message}");
            }

            // ✅ Logical (field presence) validation
            bool hasAllocID = msg.IsSetField(70);
            bool hasTradeDate = msg.IsSetField(75);
            bool hasSide = msg.IsSetField(54);
            bool hasOrders = msg.IsSetField(73);
            bool hasAllocs = msg.IsSetField(78);

            if (!hasAllocID) errors.Add("AllocID (70) missing");
            if (!hasTradeDate) errors.Add("TradeDate (75) missing");
            if (!hasSide) errors.Add("Side (54) missing");
            if (!hasOrders) errors.Add("NoOrders (73) group missing or empty");
            if (!hasAllocs) errors.Add("NoAllocs (78) group missing or empty");

            return errors;
        }


        // ================================
        // Manual Deep Copy for Message
        // ================================
        private Message CloneMessage(Message original)
        {
            var copy = new Message();
            copy.Header.CopyStateFrom(original.Header);
            copy.Trailer.CopyStateFrom(original.Trailer);

            foreach (var field in original)
                copy.SetField(field.Value);

            // Copy repeating groups
            foreach (int groupTag in original.FieldOrder)
            {
                int count = original.GroupCount(groupTag);
                for (int i = 1; i <= count; i++)
                {
                    var g = new Group(groupTag, 0);
                    original.GetGroup(i, g);
                    copy.AddGroup(g);
                }
            }

            return copy;
        }

        // ================================
        // Outbound Logging (Human Readable)
        // ================================
        private async Task LogFixMessageAsync(Message msg, string direction)
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            var logFile = Path.Combine(_logPath, $"fix_outbound_{today}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {direction} FIX.4.2 Allocation Message");
            sb.AppendLine(new string('-', 80));
            sb.AppendLine(msg.ToString().Replace("\u0001", "|"));
            sb.AppendLine(new string('-', 80));
            sb.AppendLine();

            await File.AppendAllTextAsync(logFile, sb.ToString());
            _logger.LogInformation("Logged FIX message to {File}", logFile);
        }
    }
}
