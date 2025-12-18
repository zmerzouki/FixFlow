using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.Fields;
using System.Text;

namespace RJF.TradeAllocBridge.Core.Fix
{
    public class FixClient
    {
        private readonly ILogger<FixClient> _logger;
        private readonly FixApp _app;
        private readonly string _logPath;
        private readonly QuickFix.DataDictionary.DataDictionary? _fix42Dictionary;

        private string _dictPath = "";

        public FixClient(ILogger<FixClient> logger, FixApp app)
        {
            _logger = logger;
            _app = app;

            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(_logPath);

            // Resolve FIX42.xml from cfg folder relative to executable
            _dictPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cfg", "FIX42.xml");

            try
            {
                if (File.Exists(_dictPath))
                {
                    _fix42Dictionary = new QuickFix.DataDictionary.DataDictionary(_dictPath);
                    _logger.LogInformation("✅ FIX42 dictionary loaded successfully for validation from {Path}", _dictPath);
                    _logger.LogInformation("   Fields={Fields}, Messages={Messages}",
                        _fix42Dictionary.FieldsByTag.Count, _fix42Dictionary.Messages.Count);
                }
                else
                {
                    _logger.LogWarning("⚠️ FIX42.xml not found at {Path}. Structural validation will be skipped.", _dictPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load FIX42 DataDictionary for validation.");
            }
        }

        // -------------------------------------------------------------
        // Send Message (validated, session-safe, full logging)
        // -------------------------------------------------------------
        public async Task<string> SendAsync(Message msg, SessionID? sessionID = null)
        {
            try
            {
                // 🧩 Determine session dynamically from message header
                var senderCompID = msg.Header.GetString(QuickFix.Fields.Tags.SenderCompID);
                var targetCompID = msg.Header.GetString(QuickFix.Fields.Tags.TargetCompID);
                var beginString = "FIX.4.2";
                var sessionId = sessionID ?? new SessionID(beginString, senderCompID, targetCompID);
                var session = Session.LookupSession(sessionId);

                if (session == null)
                {
                    _logger.LogError("❌ No active FIX session found for {Sender}->{Target}.", senderCompID, targetCompID);
                    return "NoSession";
                }

                // ✅ Sync session headers to message
                msg.Header.SetField(new QuickFix.Fields.BeginString("FIX.4.2"));
                msg.Header.SetField(new QuickFix.Fields.MsgType(msg.Header.GetString(QuickFix.Fields.Tags.MsgType)));
                msg.Header.SetField(new QuickFix.Fields.SenderCompID(sessionId.SenderCompID));
                msg.Header.SetField(new QuickFix.Fields.TargetCompID(sessionId.TargetCompID));

                // ✅ Add required headers for validation (QuickFIX/n fills them when sending)
                string serialized = msg.ToString();
                int bodyLength = serialized.Length;
                msg.Header.SetField(new QuickFix.Fields.BodyLength(bodyLength));
                msg.Header.SetField(new QuickFix.Fields.MsgSeqNum(1));
                msg.Header.SetField(new QuickFix.Fields.SendingTime(DateTime.UtcNow));
                msg.Trailer.SetField(new QuickFix.Fields.CheckSum("000"));

                // 🔍 Validate before send
                var validationErrors = ValidateAllocation(msg);
                if (validationErrors.Any())
                {
                    var errorText = string.Join("; ", validationErrors);
                    _logger.LogError("FIX validation failed. Message not sent. Errors: {Errors}", errorText);
                    return errorText;
                }

                var msgClone = CloneMessage(msg);
                await LogFixMessageAsync(msgClone, "OUTBOUND");

                _logger.LogDebug("🔍 Preparing to send FIX message using session {SessionID}", session.SessionID);

                bool ok = Session.SendToTarget(msg, session.SessionID);
                _logger.LogInformation("📤 FIX message sent via session {Session}: {Status}",
                    session.SessionID, ok ? "OK" : "FAILED");

                // ✅ Log final serialized wire-format message
                try
                {
                    var wire = msg.ToString().Replace('\u0001', '|');
                    var today = DateTime.Now.ToString("yyyyMMdd");
                    var logFile = Path.Combine(_logPath, $"fix_wire_{today}.log");
                    await File.AppendAllTextAsync(logFile,
                        $"[{DateTime.Now:HH:mm:ss}] {session.SessionID} SENT\n{wire}\n\n");
                    _logger.LogInformation("🧾 Logged serialized wire-format FIX message to {File}", logFile);
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(logEx, "⚠️ Failed to log serialized FIX message after send.");
                }

                return ok ? "OK" : "FAILED";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending FIX message.");
                return "Error";
            }
        }

        // -------------------------------------------------------------
        // Structural + Logical Validation (FIX 4.2)
        // -------------------------------------------------------------
        private List<string> ValidateAllocation(Message msg)
        {
            var errors = new List<string>();

            if (_fix42Dictionary == null)
            {
                _logger.LogWarning("⚠️ Skipping FIX42 structural validation (dictionary not loaded).");
                return errors;
            }

            try
            {
                var msgType = msg.Header.GetString(QuickFix.Fields.Tags.MsgType);
                _logger.LogDebug("🔍 Performing FIX42 structural validation for MsgType={MsgType}", msgType);

                QuickFix.DataDictionary.DataDictionary.Validate(
                    msg,
                    null,
                    _fix42Dictionary,
                    "FIX.4.2",
                    msgType
                );

                _logger.LogDebug("✅ Structural validation passed for MsgType={MsgType}", msgType);
            }
            catch (QuickFix.RequiredTagMissing ex)
            {
                if (ex.Field > 0)
                {
                    _logger.LogWarning("❗ Missing required tag {Tag}", ex.Field);
                    errors.Add($"Required tag missing: {ex.Field}");
                }
                else
                {
                    _logger.LogWarning("⚠️ Structural validation failed: {Message}", ex.Message);
                    errors.Add($"Required tag missing: {ex.Message}");
                }
            }
            catch (QuickFix.InvalidMessage ex)
            {
                _logger.LogWarning("⚠️ Structural validation failed: Invalid message: {Message}", ex.Message);
                errors.Add($"Invalid message structure: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Unexpected structural validation error: {Message}", ex.Message);
                errors.Add($"Structural validation error: {ex.Message}");
            }

            // ✅ Logical field checks
            if (!msg.IsSetField(70)) errors.Add("AllocID (70) missing");
            if (!msg.IsSetField(71)) errors.Add("AllocTransType (71) missing");
            if (!msg.IsSetField(75)) errors.Add("TradeDate (75) missing");
            if (!msg.IsSetField(54)) errors.Add("Side (54) missing");
            if (!msg.IsSetField(73)) errors.Add("NoOrders (73) missing or empty");
            if (!msg.IsSetField(78)) errors.Add("NoAllocs (78) missing or empty");

            return errors;
        }

        // -------------------------------------------------------------
        // Deep Copy (for safe logging)
        // -------------------------------------------------------------
        private Message CloneMessage(Message original)
        {
            var copy = new Message();
            copy.Header.CopyStateFrom(original.Header);
            copy.Trailer.CopyStateFrom(original.Trailer);

            foreach (var field in original)
                copy.SetField(field.Value);

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

        // -------------------------------------------------------------
        // Log Outbound FIX Message (pre-send)
        // -------------------------------------------------------------
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
            _logger.LogInformation("📝 Logged FIX message to {File}", logFile);
        }
    }
}
