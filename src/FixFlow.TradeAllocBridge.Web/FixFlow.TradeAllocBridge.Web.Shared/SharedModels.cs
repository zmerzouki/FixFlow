namespace FixFlow.TradeAllocBridge.Web.Shared;

public class IngestionPreviewRequest
{
    public string ClientId { get; set; } = "DEFAULT";
    public bool DryRun { get; set; } = true;
    public string? OnBehalfOfCompId { get; set; }
}

public record SampleFileOption(
    string FileName,
    long SizeKb);

public record SamplePreviewResponse(
    string FileName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int TotalRows,
    int PreviewRows);

public record FixMessagePreview(
    string AllocId,
    string Symbol,
    string Side,
    int TradeCount,
    string RawFix,
    string Status,
    string ErrorMessage);

public record IngestionPreviewResponse(
    string Status,
    int TotalTrades,
    int ValidTrades,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<FixMessagePreview> Messages,
    int AllocationsSubmitted,
    int AllocationsProcessed,
    int AllocationsSent,
    int SuccessfulAllocations,
    int FailedAllocations,
    string? ResultSuccessMessage,
    string? ResultFailureMessage);

public record MessageLogEntry(
    DateTime Timestamp,
    string ClientId,
    string AllocId,
    string Symbol,
    string Side,
    string ProcessingStatus,
    string ErrorDetails,
    string RawFixMessage,
    string Source,
    string ReportFile);

public record FixDictionaryEnumValue(
    string Value,
    string Description);

public record FixDictionaryTag(
    int Tag,
    string Name,
    string Type,
    string Description,
    bool? IsRequired,
    IReadOnlyList<FixDictionaryEnumValue> Enums,
    IReadOnlyList<string> MessagesUsing,
    int Depth,
    int Order);

public record FixDictionaryOption(
    string Key,
    string Display);

public record FixDictionaryMessageOption(
    string Name,
    string MsgType,
    string Display);

public record FixDictionaryFieldOption(
    string Name,
    string Number,
    string Type,
    IReadOnlyList<FixDictionaryEnumValue> Enums,
    string Display);

public record FixDictionaryLoadResult(
    string StatusMessage,
    FixDictionaryMessageOption? DefaultMessage);

public record FixDictionaryLookupResult(
    string TagHeader,
    string RequiredMessageInfo,
    string RequiredMessageAnswer,
    bool IsRequiredInMessage,
    IReadOnlyList<string> TagEnumResults,
    IReadOnlyList<string> TagMessageResults);

public record MappingOption(
    string ClientId,
    string DisplayName,
    bool IsValidated = false,
    string? DateValidated = null,
    string? SenderDomain = null);

public record MappingDetails(
    string ClientId,
    string? SenderDomain,
    string? FixSenderCompId,
    string? FixTargetCompId,
    string? OnBehalfOfCompId,
    int MappedFieldsCount);

public record MappingFieldDetail(
    string Tag,
    string? TagName,
    string ColumnName,
    bool IsRequired);

public record FixFieldOption(
    string Name,
    string Tag,
    string Display);

public record FixTagMetadata(
    string Tag,
    string? Name,
    string Level);

public record MappingFieldEntry(
    string ColumnName,
    string Tag,
    string TagDisplay,
    string TagLevel);

public record MappingDefaultValue(
    string Tag,
    string Value);

public record MappingFieldDefaultValue(
    string ColumnName,
    string Tag,
    string Value);

public record MappingDefaultsResponse(
    string SenderCompId,
    string TargetCompId,
    string TargetSubId,
    string OnBehalfOfCompId,
    string DeliverToCompId,
    IReadOnlyList<MappingFieldInput> DefaultFields);

public record MappingFieldInput(
    string ColumnName,
    string Tag);

public record MappingEditorDto(
    string ClientId,
    string OrganizationName,
    string SenderDomain,
    string SenderCompId,
    string TargetCompId,
    string TargetSubId,
    string OnBehalfOfCompId,
    string DeliverToCompId,
    IReadOnlyList<MappingFieldEntry> Fields,
    IReadOnlyList<MappingDefaultValue> Defaults,
    IReadOnlyList<MappingFieldDefaultValue> FieldDefaults,
    string DateCreated,
    string DateValidated,
    string DateModified,
    string EmailAutomationStatus,
    bool HasDeployedCopy);

public record MappingSaveRequest(
    string? OriginalClientId,
    string ClientId,
    string OrganizationName,
    string SenderDomain,
    string SenderCompId,
    string TargetCompId,
    string TargetSubId,
    string OnBehalfOfCompId,
    string DeliverToCompId,
    IReadOnlyList<MappingFieldInput> Fields,
    IReadOnlyList<MappingDefaultValue> Defaults,
    IReadOnlyList<MappingFieldDefaultValue> FieldDefaults);

public record MappingSaveResponse(
    bool Success,
    string StatusMessage,
    MappingEditorDto? Mapping,
    IReadOnlyList<string> MissingRequiredTags);

public record MappingDeleteResponse(
    bool Success,
    string StatusMessage);

public record MappingDeployResponse(
    bool Success,
    string StatusMessage,
    string? EmailAutomationStatus);

public record AppSettingItemDto(
    string Section,
    string DisplayKey,
    string DisplayValue,
    string Path,
    bool IsSecret,
    string Value);

public record FixDefaultItemDto(
    string Key,
    string Value);

public record SettingsLoadResponse(
    string StatusMessage,
    IReadOnlyList<AppSettingItemDto> Settings,
    string FixDefaultsStatusMessage,
    IReadOnlyList<FixDefaultItemDto> FixDefaults,
    string? AppSettingsLastModified,
    string? FixDefaultsLastModified);

public record SettingsUpdateItem(
    string Path,
    string Value,
    bool IsSecret,
    bool HasNewSecret);

public record SettingsSaveRequest(
    IReadOnlyList<SettingsUpdateItem> Settings,
    IReadOnlyList<FixDefaultItemDto> FixDefaults,
    bool SaveAppSettings,
    bool SaveFixDefaults);

public record SettingsSaveResponse(
    bool Success,
    string StatusMessage,
    string FixDefaultsStatusMessage);
