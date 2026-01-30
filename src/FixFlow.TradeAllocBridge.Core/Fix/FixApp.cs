using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.Fields;
using QuickFix.FIX42;
using FixFlow.TradeAllocBridge.Core.Reporting;

namespace FixFlow.TradeAllocBridge.Core.Fix
{
    public class FixApp : MessageCracker, IApplication
    {
        private readonly ILogger<FixApp> _logger;
        private readonly ValidationReport _validationReport;
        private Session? _activeSession;
        public FixEngine? Engine { get; set; }

        public FixApp(ILogger<FixApp> logger, ValidationReport validationReport)
        {
            _logger = logger;
            _validationReport = validationReport;
        }

        public Session? ActiveSession => _activeSession;

        public void OnCreate(SessionID sessionID)
        {
            _logger.LogInformation("Session created {Session}", sessionID);
            _activeSession = Session.LookupSession(sessionID);
        }

        public void OnLogon(SessionID sessionID) =>
            _logger.LogInformation("Logon {Session}", sessionID);

        public void OnLogout(SessionID sessionID) =>
            _logger.LogInformation("Logout {Session}", sessionID);

        public void ToAdmin(QuickFix.Message message, SessionID sessionID) =>
            _logger.LogDebug("[Admin-Out] {Message}", SafeFixPreview(message));

        public void FromAdmin(QuickFix.Message message, SessionID sessionID) =>
            _logger.LogInformation("[Admin-In] {Message}", SafeFixPreview(message));

        public void ToApp(QuickFix.Message message, SessionID sessionID)
        {
            try
            {
                string msgType = message.Header.GetString(Tags.MsgType);
                string msgName = GetMsgName(msgType);
                var reportsDir = Path.Combine(AppContext.BaseDirectory, "reports");
                Directory.CreateDirectory(reportsDir);

                string logPath = Path.Combine(reportsDir, $"outbound_fix_{DateTime.UtcNow:yyyyMMdd}.log");
                string readablePath = logPath.Replace(".log", "_readable.log");

                string rawFix = message.ToString().Replace('\u0001', '\x01');
                string readable = rawFix.Replace('\x01', '|');

                File.AppendAllText(logPath, $"{DateTime.UtcNow:O} [35={msgType}] {msgName}\n{rawFix}\n\n", Encoding.ASCII);
                File.AppendAllText(readablePath, $"{DateTime.UtcNow:O} [35={msgType}] {msgName}\n{readable}\n\n");

                _logger.LogInformation("[Outbound FIX] [35={MsgType}] {MsgName}", msgType, msgName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log outbound FIX message.");
            }
        }

        public void FromApp(QuickFix.Message message, SessionID sessionID)
        {
            try
            {
                string msgType = message.Header.GetString(Tags.MsgType);
                string msgName = GetMsgName(msgType);

                var reportsDir = Path.Combine(AppContext.BaseDirectory, "reports");
                Directory.CreateDirectory(reportsDir);

                string logPath = Path.Combine(reportsDir, $"inbound_fix_{DateTime.UtcNow:yyyyMMdd}.log");
                string readablePath = logPath.Replace(".log", "_readable.log");

                string rawFix = message.ToString().Replace('\u0001', '\x01');
                string readable = rawFix.Replace('\x01', '|');

                File.AppendAllText(logPath, $"{DateTime.UtcNow:O} [35={msgType}] {msgName}\n{rawFix}\n\n", Encoding.ASCII);
                File.AppendAllText(readablePath, $"{DateTime.UtcNow:O} [35={msgType}] {msgName}\n{readable}\n\n");

                _logger.LogInformation("[Inbound FIX] [35={MsgType}] {MsgName}", msgType, msgName);

                try
                {
                    // Try to dispatch message through MessageCracker
                    Crack(message, sessionID);
                }
                catch (QuickFix.UnsupportedMessageType)
                {
                    // ✅ Gracefully handle 35=j or other unknown message types
                    if (msgType == "j")
                    {
                        _logger.LogInformation("📩 Received BusinessMessageReject (35=j): {Readable}", readable);
                    }
                    else
                    {
                        _logger.LogInformation("ℹ️ Received unhandled FIX message [35={MsgType}] — no cracker handler defined.", msgType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing inbound FIX message.");
            }
        }


        // ===============================================================
        // 🔹 AllocationInstructionAck Handler (MsgType = 'P' in FIX4.2)
        // ===============================================================
        public void OnMessage(QuickFix.FIX42.AllocationACK message, SessionID sessionID)
        {
            HandleAllocationAck(message, sessionID);
        }   

            private void HandleAllocationAck(QuickFix.Message message, SessionID sessionID)
        {
            try
            {
                string allocID = message.IsSetField(70) ? message.GetString(70) : "(unknown)";
                string status = message.IsSetField(87) ? message.GetString(87) : "(no status)";
                string text = message.IsSetField(58) ? message.GetString(58) : "";

                string readableStatus = status switch
                {
                    "0" => "Accepted",
                    "1" => "Rejected",
                    "2" => "Partial Accept",
                    _ => $"Unknown({status})"
                };

                _logger.LogInformation("📥 Received AllocationInstructionAck (35=P): AllocID={AllocID}, Status={Status}, Text={Text}",
                    allocID, readableStatus, text);

                // ✅ Record in validation report
                _validationReport.Add(new ValidationResult(
                    DateTime.Now.ToString("o"),
                    message.IsSetField(49) ? message.GetString(49) : string.Empty,
                    FormatAllocId(allocID, NormalizeTradeDate(message.IsSetField(75) ? message.GetString(75) : string.Empty)),
                    message.IsSetField(54) ? message.GetString(54) : string.Empty,
                    message.IsSetField(55) ? message.GetString(55) : string.Empty,
                    readableStatus,
                    string.Empty,
                    message.ToString().Replace('\u0001', '|')
                ));

                _validationReport.Save();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing AllocationInstructionAck (35=P)");
            }
        }
        // ===============================================================
        // 🔹 BusinessMessageReject Handler (MsgType = 'j')
        // ===============================================================
        public void OnMessage(QuickFix.Message message, SessionID sessionID)
        {
            _logger.LogInformation("ℹ️ Received unhandled FIX message [35={MsgType}]: {MsgName}",
                message.Header.GetString(Tags.MsgType),
                message.ToString().Replace('\u0001', '|'));
        }

        // ===============================================================
        // Helpers
        // ===============================================================
        private static string SafeFixPreview(QuickFix.Message msg)
        {
            try { return msg.ToString().Replace('\u0001', '|'); }
            catch { return "(unprintable FIX message)"; }
        }

        private static string GetMsgName(string msgType) =>
            msgType switch
            {
                MsgType.LOGON => "Logon",
                MsgType.LOGOUT => "Logout",
                MsgType.HEARTBEAT => "Heartbeat",
                MsgType.TEST_REQUEST => "TestRequest",
                MsgType.RESEND_REQUEST => "ResendRequest",
                MsgType.SEQUENCE_RESET => "SequenceReset",
                MsgType.REJECT => "Reject",
                "8" => "ExecutionReport",
                "D" => "NewOrderSingle",
                "F" => "OrderCancelRequest",
                "J" => "AllocationInstruction",
                "P" => "AllocationInstructionAck", // FIX4.2 Ack
                _ => $"Unknown({msgType})"
            };

        private static string NormalizeTradeDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var trimmed = raw.Trim();
            if (trimmed.Length == 8 && trimmed.All(char.IsDigit))
            {
                return trimmed;
            }

            if (DateTime.TryParse(trimmed, out var parsed))
            {
                return parsed.ToString("yyyyMMdd");
            }

            return string.Empty;
        }

        private static string FormatAllocId(string allocId, string tradeDate)
        {
            return string.IsNullOrWhiteSpace(tradeDate) ? allocId : $"{allocId}_{tradeDate}";
        }
    }
}
