namespace FixFlow.TradeAllocBridge.Core.Config;

/// <summary>
/// Configuration for FIX 4.2 session connectivity and message behavior.
/// Aligns with QuickFIX/n configuration properties.
/// </summary>
public class FixConfig
{
    // Core session identifiers (required)
    public string BeginString { get; set; } = "FIX.4.2";
    public string SenderCompId { get; set; } = "FIXFLOW"; // Tag 49
    public string TargetCompId { get; set; } = "BROKER"; // Tag 56

    // Constant parameters
    public string AllocTransType { get; set; } = "0"; // Tage 71 - New

    // Network / connection
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5001;
    public int ReconnectInterval { get; set; } = 60;

    // File store / logging
    public string FileStorePath { get; set; } = "store";
    public string FileLogPath { get; set; } = "log";
    public string LogFileName { get; set; } = "fix_log";
    public string LogLevel { get; set; } = "INFO";

    // Session timing
    public string StartTime { get; set; } = "00:00:00";
    public string EndTime { get; set; } = "23:59:59";
    public string HeartBtInt { get; set; } = "30";

    // Session behavior
    public bool ResetOnLogout { get; set; } = true;
    public bool ResetOnDisconnect { get; set; } = true;
    public bool ResetOnLogon { get; set; } = false;
    public bool UseDataDictionary { get; set; } = true;
    public bool ValidateFieldsOutOfOrder { get; set; } = false;
    public bool ValidateFieldsHaveValues { get; set; } = false;
    public bool ContinueInitializationOnError { get; set; } = true;
    public bool CheckLatency { get; set; } = false;
    public bool UseLocalTime { get; set; } = true;

    // Message storage & sequence
    public bool PersistMessages { get; set; } = true;
    public bool RefreshOnLogon { get; set; } = false;
    public bool UseClosedStore { get; set; } = false;
    public bool ResetOnError { get; set; } = true;
    public bool ResetSeqNumFlag { get; set; } = false;

    // Data dictionary paths
    public string DataDictionary { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");

    // Security / authentication
    public string? Username { get; set; }
    public string? Password { get; set; }
    
    // Security / SSL (optional)
    public bool UseSsl { get; set; } = false;
    public string? SslCertificatePath { get; set; }
    public string? SslCertificatePassword { get; set; }
    public string? SslCaFile { get; set; }
    public bool SslVerifyPeer { get; set; } = true;

    // Utility
    public string SessionQualifier => $"{SenderCompId}-{TargetCompId}-{BeginString}";
}
