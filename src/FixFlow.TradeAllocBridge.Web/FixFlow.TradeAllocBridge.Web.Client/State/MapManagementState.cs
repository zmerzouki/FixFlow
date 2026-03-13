using FixFlow.TradeAllocBridge.Web.Shared;

namespace FixFlow.TradeAllocBridge.Web.Client.State;

public class MapManagementState
{
    public List<MappingOption> MappingOptions { get; } = new();
    public bool HasLoadedMappings { get; set; }
    public bool HasLoadedFixFields { get; set; }
    public bool HasLoadedTagMetadata { get; set; }

    public string? SelectedMapping { get; set; }
    public string? OriginalClientId { get; set; }
    public bool IsEditing { get; set; }
    public bool IsFirstVisit { get; set; } = true;
    public bool HasUnsavedChanges { get; set; }

    public string StatusMessage { get; set; } = "Ready";

    public string ClientId { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string SenderDomain { get; set; } = string.Empty;
    public string SenderCompId { get; set; } = string.Empty;
    public string TargetCompId { get; set; } = string.Empty;
    public string TargetSubId { get; set; } = string.Empty;
    public string OnBehalfOfCompId { get; set; } = string.Empty;
    public string DeliverToCompId { get; set; } = string.Empty;
    public string DateCreated { get; set; } = string.Empty;
    public string DateValidated { get; set; } = string.Empty;
    public string DateModified { get; set; } = string.Empty;
    public string EmailAutomationStatus { get; set; } = "Inactive";
    public bool HasDeployedCopy { get; set; }

    public List<MappingFieldEntry> FieldMappings { get; } = new();
    public MappingFieldEntry? SelectedFieldMapping { get; set; }
    public Dictionary<string, string> DefaultTagValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> FieldDefaultTagValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string NewFieldName { get; set; } = string.Empty;
    public string NewFieldTag { get; set; } = string.Empty;
    public string NewFieldTagInput { get; set; } = string.Empty;
    public List<FixFieldOption> FixFieldOptions { get; } = new();
    public List<FixFieldOption> FixFieldSuggestions { get; } = new();
    public bool IsFixFieldSuggestionOpen { get; set; }

    public Dictionary<string, FixTagMetadata> TagMetadata { get; } = new();
}
