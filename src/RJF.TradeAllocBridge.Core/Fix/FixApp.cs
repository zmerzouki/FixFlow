using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.Fields;

namespace RJF.TradeAllocBridge.Core.Fix
{
    public class FixApp : MessageCracker, IApplication
    {
        private readonly ILogger<FixApp> _logger;
        private Session? _activeSession;
        public FixEngine? Engine { get; set; }


        public FixApp(ILogger<FixApp> logger)
        {
            _logger = logger;
        }

        public Session? ActiveSession => _activeSession;

        public void OnCreate(SessionID sessionID)
        {
            _logger.LogInformation("Session created {Session}", sessionID);
            _activeSession = Session.LookupSession(sessionID);
        }

        public void OnLogon(SessionID sessionID)
        {
            _logger.LogInformation("Logon {Session}", sessionID);
        }

        public void OnLogout(SessionID sessionID)
        {
            _logger.LogInformation("Logout {Session}", sessionID);
        }

        public void ToAdmin(Message message, SessionID sessionID)
        {
            _logger.LogDebug("[Admin-Out] {Message}", SafeFixPreview(message));
        }

        public void FromAdmin(Message message, SessionID sessionID)
        {
            _logger.LogInformation("[Admin-In] {Message}", SafeFixPreview(message));
        }

        public void ToApp(Message message, SessionID sessionID)
        {
            try
            {
                var msgType = message.Header.GetString(Tags.MsgType);
                var msgName = GetMsgName(msgType);

                var reportsDir = Path.Combine(AppContext.BaseDirectory, "reports");
                Directory.CreateDirectory(reportsDir);

                string logPath = Path.Combine(reportsDir, $"outbound_fix_{DateTime.UtcNow:yyyyMMdd}.log");
                string readablePath = logPath.Replace(".log", "_readable.log");

                string rawFix = message.ToString().Replace('\u0001', '\x01'); // true SOH
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

        public void FromApp(Message message, SessionID sessionID)
        {
            try
            {
                var msgType = message.Header.GetString(Tags.MsgType);
                var msgName = GetMsgName(msgType);

                var reportsDir = Path.Combine(AppContext.BaseDirectory, "reports");
                Directory.CreateDirectory(reportsDir);

                string logPath = Path.Combine(reportsDir, $"inbound_fix_{DateTime.UtcNow:yyyyMMdd}.log");
                string readablePath = logPath.Replace(".log", "_readable.log");

                string rawFix = message.ToString().Replace('\u0001', '\x01');
                string readable = rawFix.Replace('\x01', '|');

                File.AppendAllText(logPath, $"{DateTime.UtcNow:O} [35={msgType}] {msgName}\n{rawFix}\n\n", Encoding.ASCII);
                File.AppendAllText(readablePath, $"{DateTime.UtcNow:O} [35={msgType}] {msgName}\n{readable}\n\n");

                _logger.LogInformation("[Inbound FIX] [35={MsgType}] {MsgName}", msgType, msgName);

                Crack(message, sessionID); // optional if you define message-specific handlers
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing inbound FIX message.");
            }
        }

        private static string SafeFixPreview(Message msg)
        {
            try
            {
                return msg.ToString().Replace('\u0001', '|');
            }
            catch
            {
                return "(unprintable FIX message)";
            }
        }

        private static string GetMsgName(string msgType)
        {
            // You can extend this mapping as needed
            return msgType switch
            {
                MsgType.LOGON => "Logon",
                MsgType.LOGOUT => "Logout",
                MsgType.HEARTBEAT => "Heartbeat",
                MsgType.TEST_REQUEST => "TestRequest",
                MsgType.RESEND_REQUEST => "ResendRequest",
                MsgType.SEQUENCE_RESET => "SequenceReset",
                MsgType.REJECT => "Reject",
                "D" => "NewOrderSingle",
                "F" => "OrderCancelRequest",
                "8" => "ExecutionReport",
                "J" => "Allocation",
                _ => $"Unknown({msgType})"
            };
        }
    }
}
