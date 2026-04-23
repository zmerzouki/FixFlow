using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Components.Forms;

namespace FixFlow.TradeAllocBridge.Web.Client.State;

public class DirectIngestionState
{
    public IngestionPreviewRequest Request { get; set; } = new() { ClientId = string.Empty };
    public IngestionPreviewResponse? Preview { get; set; }
    public MappingDetails? MappingDetails { get; set; }
    public List<MappingOption> MappingOptions { get; } = new();
    public bool HasLoadedMappings { get; set; }

    public IBrowserFile? SelectedFile { get; set; }
    public string? SelectedFileName { get; set; }
    public long SelectedFileSizeKb { get; set; }
    public byte[]? SelectedFileBytes { get; set; }
    public string? SelectedFileContentType { get; set; }
    public string? FileErrorMessage { get; set; }
    public string? SelectedSampleName { get; set; }

    public bool IsBusy { get; set; }
    public bool IsDragActive { get; set; }
    public int ProgressPercent { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public string StatusMessage { get; set; } = "Ready";

    public bool ShowMappingFields { get; set; }
    public bool IsMappingFieldsLoading { get; set; }
    public string? MappingFieldsClientId { get; set; }
    public List<MappingFieldDetail> MappingFields { get; } = new();
}
