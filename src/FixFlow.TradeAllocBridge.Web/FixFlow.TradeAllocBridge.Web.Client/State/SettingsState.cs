namespace FixFlow.TradeAllocBridge.Web.Client.State;

public class SettingsState
{
    public List<SettingsItemView> Settings { get; } = new();
    public List<FixDefaultView> FixDefaults { get; } = new();

    public string StatusMessage { get; set; } = "Ready";
    public string FixDefaultsStatusMessage { get; set; } = "Ready";
    public string? AppSettingsLastModified { get; set; }
    public string? FixDefaultsLastModified { get; set; }
    public bool HasLoaded { get; set; }
}

public class SettingsItemView
{
    public string Section { get; set; } = string.Empty;
    public string DisplayKey { get; set; } = string.Empty;
    public string DisplayValue { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public string Value { get; set; } = string.Empty;
    public string OriginalValue { get; set; } = string.Empty;
    public string EditValue { get; set; } = string.Empty;
    public bool HasNewSecret { get; set; }
}

public class FixDefaultView
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string OriginalValue { get; set; } = string.Empty;
}
