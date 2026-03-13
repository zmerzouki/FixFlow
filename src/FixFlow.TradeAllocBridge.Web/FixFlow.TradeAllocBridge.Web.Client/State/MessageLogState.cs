using FixFlow.TradeAllocBridge.Web.Shared;

namespace FixFlow.TradeAllocBridge.Web.Client.State;

public class MessageLogState
{
    public string SourceKey { get; set; } = "Direct";
    public string ClientId { get; set; } = string.Empty;
    public string StatusFilter { get; set; } = string.Empty;
    public string StatusOptionsSource { get; set; } = "Direct";

    public List<MessageLogEntry> Messages { get; } = new();
    public List<MappingOption> MappingOptions { get; } = new();
    public List<string> StatusOptions { get; } = new();

    public bool HasLoadedMappings { get; set; }
    public bool HasLoadedStatuses { get; set; }
    public bool HasLoadedMessages { get; set; }
}
