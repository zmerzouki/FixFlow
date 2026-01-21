using FixFlow.TradeAllocBridge.Core.Mapping;
namespace FixFlow.TradeAllocBridge.Core.Config;

public class AppConfig
{
    public EmailConfig Email { get; set; } = new();
    public FixConfig Fix { get; set; } = new();
    public MappingConfig Mapping { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public class EmailConfig
{
    public string Provider { get; set; } = "GraphAPI";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string MailboxAddress { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 60;
    public string DownloadPath { get; set; } = "attachments";
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public string Path { get; set; } = "logs/log-.txt";
}
