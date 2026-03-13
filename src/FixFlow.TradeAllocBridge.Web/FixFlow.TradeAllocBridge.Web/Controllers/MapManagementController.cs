
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Mapping;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/map-management")]
public class MapManagementController : ControllerBase
{
    private static readonly string[] RequiredFixTags = { "54", "79", "80", "153" };
    private static readonly string[] Fix42FieldNames =
    {
        "Side",
        "Symbol",
        "SymbolSfx",
        "SecurityID",
        "IDSource",
        "SecurityType",
        "MaturityMonthYear",
        "MaturityDay",
        "PutOrCall",
        "StrikePrice",
        "OptAttribute",
        "ContractMultiplier",
        "CouponRate",
        "SecurityExchange",
        "Issuer",
        "EncodedIssuerLen",
        "EncodedIssuer",
        "SecurityDesc",
        "EncodedSecurityDescLen",
        "EncodedSecurityDesc",
        "Shares",
        "LastMkt",
        "TradingSessionID",
        "AvgPx",
        "Currency",
        "AvgPrxPrecision",
        "TradeDate",
        "TransactTime",
        "SettlmntTyp",
        "FutSettDate",
        "GrossTradeAmt",
        "NetMoney",
        "OpenClose",
        "Text",
        "EncodedTextLen",
        "EncodedText",
        "NumDaysInterest",
        "AccruedInterestRate",
        "AllocAccount",
        "AllocPrice",
        "AllocShares",
        "ProcessCode",
        "BrokerOfCredit",
        "NotifyBrokerOfCredit",
        "AllocHandlInst",
        "AllocText",
        "EncodedAllocTextLen",
        "EncodedAllocText",
        "ExecBroker",
        "ClientID",
        "Commission",
        "CommType",
        "AllocAvgPx",
        "AllocNetMoney",
        "SettlCurrAmt",
        "SettlCurrency",
        "SettlCurrFxRate",
        "SettlCurrFxRateCalc",
        "AccruedInterestAmt",
        "SettlInstMode",
        "MiscFeeAmt",
        "MiscFeeCurr",
        "MiscFeeType"
    };

    private static readonly Lazy<IReadOnlyList<FixFieldOption>> FixFieldOptionsCache = new(LoadFixFieldOptions);
    private static readonly Lazy<FixTagCache> FixTagCacheStore = new(LoadFixTagCache);

    private readonly FixMappingRepository _mappingRepo;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MapManagementController> _logger;

    public MapManagementController(
        FixMappingRepository mappingRepo,
        IConfiguration configuration,
        ILogger<MapManagementController> logger)
    {
        _mappingRepo = mappingRepo;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("fix-fields")]
    public ActionResult<IReadOnlyList<FixFieldOption>> GetFixFieldOptions()
    {
        return Ok(FixFieldOptionsCache.Value);
    }

    [HttpGet("tag-metadata")]
    public ActionResult<IReadOnlyList<FixTagMetadata>> GetTagMetadata()
    {
        var cache = FixTagCacheStore.Value;
        var results = cache.TagNames
            .Select(kvp =>
            {
                var level = cache.TagLevels.TryGetValue(kvp.Key, out var found) ? found : "Unknown";
                return new FixTagMetadata(kvp.Key, kvp.Value, level);
            })
            .ToList();

        return Ok(results);
    }

    [HttpGet("defaults")]
    public ActionResult<MappingDefaultsResponse> GetDefaults()
    {
        var defaults = LoadFixDefaults();
        var fields = new List<MappingFieldInput>
        {
            new("Side", "54"),
            new("Symbol", "55"),
            new("Trade Date", "75"),
            new("Allocation Account", "79"),
            new("Shares Qty", "80"),
            new("Average Price", "153")
        };

        return Ok(new MappingDefaultsResponse(
            defaults.SenderCompID ?? string.Empty,
            defaults.TargetCompID ?? string.Empty,
            defaults.TargetSubID ?? string.Empty,
            defaults.OnBehalfOfCompID ?? string.Empty,
            defaults.DeliverToCompID ?? string.Empty,
            fields));
    }

    [HttpGet("mapping/{clientId}")]
    public ActionResult<MappingEditorDto> GetMapping(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return BadRequest("Client ID is required.");
        }

        var mapPath = Path.Combine(_mappingRepo.BaseDirectory, $"{clientId}_map.json");
        if (!System.IO.File.Exists(mapPath))
        {
            return NotFound();
        }

        try
        {
            var mapping = MappingConfig.Load(mapPath);
            return Ok(BuildEditorDto(mapping, mapPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load mapping for {ClientId}", clientId);
            return Problem($"Failed to load mapping: {ex.Message}");
        }
    }

    [HttpPost("save")]
    public ActionResult<MappingSaveResponse> SaveMapping([FromBody] MappingSaveRequest request)
    {
        if (request == null)
        {
            return BadRequest("Request is required.");
        }

        var clientId = NormalizeUpper(request.ClientId);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Ok(new MappingSaveResponse(false, "Client ID is required", null, Array.Empty<string>()));
        }

        if (request.Fields == null || request.Fields.Count == 0)
        {
            return Ok(new MappingSaveResponse(false, "At least one field mapping is required", null, Array.Empty<string>()));
        }

        var missingRequired = RequiredFixTags
            .Where(tag => request.Fields.All(f => !string.Equals(f.Tag?.Trim(), tag, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var hasTag55 = request.Fields.Any(f => string.Equals(f.Tag?.Trim(), "55", StringComparison.OrdinalIgnoreCase));
        var hasTag48 = request.Fields.Any(f => string.Equals(f.Tag?.Trim(), "48", StringComparison.OrdinalIgnoreCase));
        if (!hasTag55 && !hasTag48)
        {
            missingRequired.Add("55 or 48");
        }
        if (missingRequired.Count > 0)
        {
            return Ok(new MappingSaveResponse(false,
                "Missing required FIX fields. Please map tags 54, 79, 80, 153, and either 55 or 48 before saving.",
                null,
                missingRequired));
        }

        var duplicateClient = _mappingRepo
            .GetAll()
            .Any(m => string.Equals(m.ClientId, clientId, StringComparison.OrdinalIgnoreCase));

        if (duplicateClient &&
            !string.Equals(request.OriginalClientId, clientId, StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new MappingSaveResponse(false,
                $"The map for {clientId} already exists. {clientId} map must first be deleted before creating a new one with the same name.",
                null,
                Array.Empty<string>()));
        }

        var senderDomain = NormalizeUpper(request.SenderDomain);
        if (!string.IsNullOrWhiteSpace(senderDomain))
        {
            var conflicting = _mappingRepo
                .GetAll()
                .FirstOrDefault(m =>
                    !string.IsNullOrWhiteSpace(m.SenderDomain) &&
                    string.Equals(NormalizeUpper(m.SenderDomain), senderDomain, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(m.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(m.ClientId, request.OriginalClientId, StringComparison.OrdinalIgnoreCase));

            if (conflicting != null)
            {
                return Ok(new MappingSaveResponse(false,
                    $"Sender domain {senderDomain} is already used by mapping {conflicting.ClientId}.",
                    null,
                    Array.Empty<string>()));
            }
        }

        try
        {
            var webQualifier = NormalizeQualifier(ResolveSessionQualifier("Web", "FixFlowWeb"));
            var mapping = new MappingConfig
            {
                ClientId = clientId,
                OrganizationName = string.IsNullOrWhiteSpace(request.OrganizationName) ? string.Empty : request.OrganizationName.Trim(),
                SenderDomain = senderDomain,
                Predefined = new PredefinedFields
                {
                    SenderCompID = NormalizeUpper(request.SenderCompId),
                    SenderSubID = string.IsNullOrWhiteSpace(webQualifier) ? null : webQualifier,
                    TargetCompID = string.IsNullOrWhiteSpace(request.TargetCompId) ? "BROKER" : NormalizeUpper(request.TargetCompId),
                    TargetSubID = string.IsNullOrWhiteSpace(request.TargetSubId) ? null : NormalizeUpper(request.TargetSubId),
                    OnBehalfOfCompID = string.IsNullOrWhiteSpace(request.OnBehalfOfCompId) ? null : NormalizeUpper(request.OnBehalfOfCompId),
                    DeliverToCompID = string.IsNullOrWhiteSpace(request.DeliverToCompId) ? null : NormalizeUpper(request.DeliverToCompId)
                },
                TradeAllocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                DefaultTagValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                FieldDefaultTagValues = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
                DateValidated = null
            };

            foreach (var field in request.Fields)
            {
                var header = field.ColumnName?.Trim() ?? string.Empty;
                var tag = field.Tag?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(header) && !string.IsNullOrWhiteSpace(tag))
                {
                    mapping.TradeAllocations[header] = tag;
                }
            }

            if (request.Defaults != null)
            {
                foreach (var entry in request.Defaults)
                {
                    var tag = entry.Tag?.Trim() ?? string.Empty;
                    var value = entry.Value?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(tag) && !string.IsNullOrWhiteSpace(value))
                    {
                        mapping.DefaultTagValues[tag] = value;
                    }
                }
            }

            if (request.FieldDefaults != null)
            {
                foreach (var entry in request.FieldDefaults)
                {
                    var column = entry.ColumnName?.Trim() ?? string.Empty;
                    var tag = entry.Tag?.Trim() ?? string.Empty;
                    var value = entry.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(column) || string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (!mapping.FieldDefaultTagValues.TryGetValue(column, out var tagDefaults))
                    {
                        tagDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        mapping.FieldDefaultTagValues[column] = tagDefaults;
                    }

                    tagDefaults[tag] = value;
                }
            }

            var filename = $"{clientId}_map.json";
            var filepath = Path.Combine(_mappingRepo.BaseDirectory, filename);

            var json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            System.IO.File.WriteAllText(filepath, json);
            UpdateSessionConfigFiles(mapping);

            _mappingRepo.NotifyMappingsChanged();

            var dto = BuildEditorDto(mapping, filepath);
            return Ok(new MappingSaveResponse(true, $"Mapping saved: {clientId}", dto, Array.Empty<string>()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save mapping");
            return Ok(new MappingSaveResponse(false, $"Error saving mapping: {ex.Message}", null, Array.Empty<string>()));
        }
    }

    [HttpPost("{clientId}/deploy")]
    public ActionResult<MappingDeployResponse> DeployMapping(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return BadRequest("Client ID is required.");
        }

        var filename = $"{clientId}_map.json";
        var sourcePath = Path.Combine(_mappingRepo.BaseDirectory, filename);
        if (!System.IO.File.Exists(sourcePath))
        {
            return Ok(new MappingDeployResponse(false, "Mapping file not found", null));
        }

        try
        {
            var mapping = MappingConfig.Load(sourcePath);
            if (mapping == null)
            {
                return Ok(new MappingDeployResponse(false, "Invalid mapping configuration", null));
            }
            if (string.IsNullOrWhiteSpace(mapping.SenderDomain))
            {
                return Ok(new MappingDeployResponse(false, "Sender domain is required to enable email automation.", null));
            }

            var serviceQualifier = NormalizeQualifier(ResolveSessionQualifier("Service", "FixFlowService"));
            mapping.Predefined ??= new PredefinedFields();
            mapping.Predefined.SenderSubID = string.IsNullOrWhiteSpace(serviceQualifier) ? null : serviceQualifier;

            var cliConfigsDir = ResolveCliConfigsDir();
            if (string.IsNullOrWhiteSpace(cliConfigsDir))
            {
                return Ok(new MappingDeployResponse(false, "CLI configs directory not found", null));
            }

            var incomingDir = Path.Combine(cliConfigsDir, "incoming");
            Directory.CreateDirectory(incomingDir);

            var incomingPath = Path.Combine(incomingDir, filename);
            var stagingFile = Path.Combine(incomingDir, $"{filename}.{Guid.NewGuid():N}.staging");

            var json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            System.IO.File.WriteAllText(stagingFile, json);

            if (!TryDeployMappingCopy(stagingFile, incomingPath, out var error))
            {
                return Ok(new MappingDeployResponse(false, error ?? "Failed to deploy mapping", null));
            }

            try
            {
                if (System.IO.File.Exists(stagingFile))
                {
                    System.IO.File.Delete(stagingFile);
                }
            }
            catch
            {
                // best-effort cleanup
            }

            UpdateCliSessionConfigFile(mapping, cliConfigsDir);
            var status = GetEmailAutomationStatus(cliConfigsDir, clientId);
            return Ok(new MappingDeployResponse(true, $"Mapping '{clientId}' deployed to Email Automation Service", status));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy mapping {ClientId}", clientId);
            return Ok(new MappingDeployResponse(false, $"Failed to deploy mapping: {ex.Message}", null));
        }
    }

    [HttpDelete("{clientId}")]
    public ActionResult<MappingDeleteResponse> DeleteMapping(string clientId, [FromQuery] bool deleteDeployed = false)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return BadRequest("Client ID is required.");
        }

        try
        {
            var filename = $"{clientId}_map.json";
            var filepath = Path.Combine(_mappingRepo.BaseDirectory, filename);
            var cliConfigsDir = ResolveCliConfigsDir();
            var cliPath = string.IsNullOrWhiteSpace(cliConfigsDir) ? null : Path.Combine(cliConfigsDir, filename);
            var deployedExists = cliPath != null && System.IO.File.Exists(cliPath);

            if (System.IO.File.Exists(filepath))
            {
                var mappingForDelete = LoadMappingForSessionSync(filepath, clientId);
                System.IO.File.Delete(filepath);
                if (mappingForDelete != null)
                {
                    RemoveSessionConfigFiles(mappingForDelete, clientId);
                }
            }

            if (deleteDeployed && deployedExists && cliPath != null)
            {
                System.IO.File.Delete(cliPath);
            }

            var mappingStillExists = System.IO.File.Exists(filepath);
            var deployedStillExists = deleteDeployed && cliPath != null && System.IO.File.Exists(cliPath);

            _mappingRepo.NotifyMappingsChanged();
            if (mappingStillExists || deployedStillExists)
            {
                var deleteStatusMessage = mappingStillExists
                    ? $"Delete failed: mapping file still exists at {filepath}"
                    : $"Delete failed: deployed copy still exists at {cliPath}";
                return Ok(new MappingDeleteResponse(false, deleteStatusMessage));
            }

            var finalStatusMessage = deleteDeployed && deployedExists
                ? $"Mapping and deployed copy deleted: {clientId}"
                : $"Mapping deleted: {clientId}";
            return Ok(new MappingDeleteResponse(true, finalStatusMessage));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete mapping {ClientId}", clientId);
            return Ok(new MappingDeleteResponse(false, $"Error deleting mapping: {ex.Message}"));
        }
    }

    private MappingEditorDto BuildEditorDto(MappingConfig mapping, string mapPath)
    {
        var cache = FixTagCacheStore.Value;
        var fields = new List<MappingFieldEntry>();

        foreach (var kvp in mapping.TradeAllocations)
        {
            var tag = kvp.Value?.Trim() ?? string.Empty;
            var tagDisplay = FormatTagDisplay(cache, tag);
            var tagLevel = GetTagLevel(cache, tag);
            fields.Add(new MappingFieldEntry(kvp.Key, tag, tagDisplay, tagLevel));
        }

        var metadata = GetMappingMetadata(mapPath, mapping.ClientId ?? string.Empty);
        var defaults = mapping.DefaultTagValues?
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
            .Select(kvp => new MappingDefaultValue(kvp.Key, kvp.Value ?? string.Empty))
            .OrderBy(kvp => kvp.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<MappingDefaultValue>();

        var fieldDefaults = new List<MappingFieldDefaultValue>();
        if (mapping.FieldDefaultTagValues != null)
        {
            foreach (var columnEntry in mapping.FieldDefaultTagValues)
            {
                var column = columnEntry.Key?.Trim();
                if (string.IsNullOrWhiteSpace(column))
                {
                    continue;
                }

                if (columnEntry.Value == null)
                {
                    continue;
                }

                foreach (var tagEntry in columnEntry.Value)
                {
                    var tag = tagEntry.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        continue;
                    }

                    fieldDefaults.Add(new MappingFieldDefaultValue(column, tag, tagEntry.Value ?? string.Empty));
                }
            }
        }

        fieldDefaults = fieldDefaults
            .OrderBy(entry => entry.ColumnName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new MappingEditorDto(
            mapping.ClientId ?? string.Empty,
            mapping.OrganizationName ?? string.Empty,
            mapping.SenderDomain ?? string.Empty,
            mapping.Predefined?.SenderCompID ?? string.Empty,
            mapping.Predefined?.TargetCompID ?? string.Empty,
            mapping.Predefined?.TargetSubID ?? string.Empty,
            mapping.Predefined?.OnBehalfOfCompID ?? string.Empty,
            mapping.Predefined?.DeliverToCompID ?? string.Empty,
            fields,
            defaults,
            fieldDefaults,
            metadata.DateCreated,
            mapping.DateValidated ?? string.Empty,
            metadata.DateModified,
            metadata.EmailAutomationStatus,
            metadata.HasDeployedCopy);
    }

    private (string DateCreated, string DateModified, string EmailAutomationStatus, bool HasDeployedCopy) GetMappingMetadata(
        string mapPath,
        string clientId)
    {
        var dateCreated = string.Empty;
        var dateModified = string.Empty;
        try
        {
            var info = new FileInfo(mapPath);
            dateCreated = info.CreationTime.ToString("M/d/yyyy h:mm tt");
            dateModified = info.LastWriteTime.ToString("M/d/yyyy h:mm tt");
        }
        catch
        {
            dateCreated = string.Empty;
            dateModified = string.Empty;
        }

        var cliConfigsDir = ResolveCliConfigsDir();
        var status = GetEmailAutomationStatus(cliConfigsDir, clientId, out var hasDeployed);
        return (dateCreated, dateModified, status, hasDeployed);
    }

    private static string GetEmailAutomationStatus(string? cliConfigsDir, string clientId) =>
        GetEmailAutomationStatus(cliConfigsDir, clientId, out _);

    private static string GetEmailAutomationStatus(string? cliConfigsDir, string clientId, out bool hasDeployedCopy)
    {
        hasDeployedCopy = false;
        if (string.IsNullOrWhiteSpace(cliConfigsDir))
        {
            return "Inactive";
        }

        try
        {
            var cliMap = Path.Combine(cliConfigsDir, $"{clientId}_map.json");
            if (System.IO.File.Exists(cliMap))
            {
                var deployTime = System.IO.File.GetLastWriteTime(cliMap);
                hasDeployedCopy = true;
                return $"Active as of {deployTime:M/d/yyyy h:mm tt}";
            }
        }
        catch
        {
            return "Inactive";
        }

        return "Inactive";
    }

    private static string NormalizeUpper(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private static string? NormalizeQualifier(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string ResolveSessionQualifier(string key, string fallback)
    {
        var value = _configuration[$"FixSessionQualifiers:{key}"];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool MatchesOptional(string? existingValue, string? desiredValue)
    {
        if (string.IsNullOrWhiteSpace(desiredValue))
        {
            return string.IsNullOrWhiteSpace(existingValue);
        }

        return string.Equals(existingValue?.Trim(), desiredValue.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private PredefinedFields LoadFixDefaults()
    {
        return new PredefinedFields
        {
            SenderCompID = GetFixSetting("49"),
            TargetCompID = GetFixSetting("56"),
            TargetSubID = GetFixSetting("57"),
            OnBehalfOfCompID = GetFixSetting("115"),
            DeliverToCompID = GetFixSetting("128")
        };
    }

    private string GetFixSetting(string key)
    {
        var value = _configuration[$"Fix:{key}"];
        return value?.Trim() ?? string.Empty;
    }

    private static string FormatTagDisplay(FixTagCache cache, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        if (cache.TagNames.TryGetValue(tag.Trim(), out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return $"Tag {tag.Trim()} ({name})";
        }

        return $"Tag {tag.Trim()}";
    }

    private static string GetTagLevel(FixTagCache cache, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "Unknown";
        }

        return cache.TagLevels.TryGetValue(tag.Trim(), out var level) ? level : "Unknown";
    }

    private static IReadOnlyList<FixFieldOption> LoadFixFieldOptions()
    {
        var options = new List<FixFieldOption>();
        var dictionaryPath = ResolveFixDictionaryPath();
        if (string.IsNullOrWhiteSpace(dictionaryPath) || !System.IO.File.Exists(dictionaryPath))
        {
            return options;
        }

        try
        {
            var doc = XDocument.Load(dictionaryPath);
            var fieldsElement = doc.Root?.Element("fields");
            if (fieldsElement == null)
            {
                return options;
            }

            var allowed = new HashSet<string>(Fix42FieldNames, StringComparer.OrdinalIgnoreCase);
            foreach (var field in fieldsElement.Elements("field"))
            {
                var name = field.Attribute("name")?.Value;
                var number = field.Attribute("number")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(number))
                {
                    continue;
                }

                if (!allowed.Contains(name))
                {
                    continue;
                }

                options.Add(new FixFieldOption(name, number, $"{name} ({number})"));
            }
        }
        catch
        {
            return options;
        }

        return options
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static FixTagCache LoadFixTagCache()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var levels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var path = ResolveFixDictionaryPath();
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return new FixTagCache(names, levels);
        }

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null)
            {
                return new FixTagCache(names, levels);
            }

            var fieldMap = root.Element("fields")?
                .Elements("field")
                .Select(f => new
                {
                    Name = (string?)f.Attribute("name"),
                    Number = (string?)f.Attribute("number")
                })
                .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Number))
                .ToDictionary(f => f.Name!, f => f.Number!, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in fieldMap)
            {
                names[kvp.Value] = kvp.Key;
            }

            var allocation = root.Element("messages")?
                .Elements("message")
                .FirstOrDefault(m => string.Equals((string?)m.Attribute("name"), "Allocation", StringComparison.OrdinalIgnoreCase));

            if (allocation != null)
            {
                foreach (var element in allocation.Elements())
                {
                    if (element.Name.LocalName == "field")
                    {
                        AddLevel(levels, fieldMap, (string?)element.Attribute("name"), "Header");
                    }
                    else if (element.Name.LocalName == "group" &&
                             string.Equals((string?)element.Attribute("name"), "NoAllocs", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var groupField in element.Elements("field"))
                        {
                            AddLevel(levels, fieldMap, (string?)groupField.Attribute("name"), "NoAllocs");
                        }

                        foreach (var nestedGroup in element.Elements("group"))
                        {
                            var groupName = (string?)nestedGroup.Attribute("name");
                            if (!string.Equals(groupName, "NoMiscFees", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            foreach (var groupField in nestedGroup.Elements("field"))
                            {
                                AddLevel(levels, fieldMap, (string?)groupField.Attribute("name"), "NoMiscFees");
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            return new FixTagCache(names, levels);
        }

        return new FixTagCache(names, levels);
    }

    private static void AddLevel(
        IDictionary<string, string> levels,
        IDictionary<string, string> fieldMap,
        string? fieldName,
        string level)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        if (!fieldMap.TryGetValue(fieldName, out var number))
        {
            return;
        }

        levels[number] = level;
    }

    private static string? ResolveFixDictionaryPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var directPath = Path.Combine(baseDir, "cfg", "FIX42.xml");
        if (System.IO.File.Exists(directPath))
        {
            return directPath;
        }

        var solutionRoot = FindAncestorWithFile(baseDir, "*.sln", maxLevels: 8);
        if (string.IsNullOrWhiteSpace(solutionRoot))
        {
            return null;
        }

        var cfgPath = Path.Combine(solutionRoot, "cfg", "FIX42.xml");
        return System.IO.File.Exists(cfgPath) ? cfgPath : null;
    }

    private void UpdateSessionConfigFiles(MappingConfig mapping)
    {
        var senderCompId = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? string.Empty;
        var targetCompId = mapping.Predefined?.TargetCompID ?? "EXECUTOR";
        var senderSubId = NormalizeQualifier(mapping.Predefined?.SenderSubID);
        var targetSubId = NormalizeQualifier(mapping.Predefined?.TargetSubID);

        if (string.IsNullOrWhiteSpace(senderCompId) || string.IsNullOrWhiteSpace(targetCompId))
        {
            return;
        }

        var wpfFixCfgPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.cfg");
        var executorCfgPath = ResolveExecutorCfgPath();

        TrySyncSessionSection(
            wpfFixCfgPath,
            senderCompId,
            targetCompId,
            senderSubId,
            targetSubId,
            BuildWpfSessionLines(senderCompId, targetCompId, senderSubId, targetSubId),
            remove: false);

        if (!string.IsNullOrWhiteSpace(executorCfgPath))
        {
            TrySyncSessionSection(
                executorCfgPath,
                targetCompId,
                senderCompId,
                targetSubId,
                senderSubId,
                BuildExecutorSessionLines(targetCompId, senderCompId, targetSubId, senderSubId),
                remove: false);
        }
    }

    private void UpdateCliSessionConfigFile(MappingConfig mapping, string cliConfigsDir)
    {
        var senderCompId = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? string.Empty;
        var targetCompId = mapping.Predefined?.TargetCompID ?? "EXECUTOR";
        var senderSubId = NormalizeQualifier(mapping.Predefined?.SenderSubID);
        var targetSubId = NormalizeQualifier(mapping.Predefined?.TargetSubID);

        if (string.IsNullOrWhiteSpace(senderCompId) || string.IsNullOrWhiteSpace(targetCompId))
        {
            return;
        }

        var cliFixCfgPath = ResolveCliFixCfgPath(cliConfigsDir);
        if (string.IsNullOrWhiteSpace(cliFixCfgPath))
        {
            _logger.LogWarning("CLI FIX42.cfg not found for session sync.");
            return;
        }

        TrySyncSessionSection(
            cliFixCfgPath,
            senderCompId,
            targetCompId,
            senderSubId,
            targetSubId,
            BuildWpfSessionLines(senderCompId, targetCompId, senderSubId, targetSubId),
            remove: false);
    }

    private void RemoveSessionConfigFiles(MappingConfig mapping, string clientId)
    {
        var senderCompId = mapping.Predefined?.SenderCompID
            ?? mapping.ClientId
            ?? clientId
            ?? string.Empty;
        var targetCompId = mapping.Predefined?.TargetCompID ?? "EXECUTOR";
        var senderSubId = NormalizeQualifier(mapping.Predefined?.SenderSubID);
        var targetSubId = NormalizeQualifier(mapping.Predefined?.TargetSubID);

        if (string.IsNullOrWhiteSpace(senderCompId))
        {
            return;
        }

        var wpfFixCfgPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.cfg");
        var executorCfgPath = ResolveExecutorCfgPath();

        TrySyncSessionSection(
            wpfFixCfgPath,
            senderCompId,
            targetCompId,
            senderSubId,
            targetSubId,
            BuildWpfSessionLines(senderCompId, targetCompId, senderSubId, targetSubId),
            remove: true);

        if (!string.IsNullOrWhiteSpace(executorCfgPath))
        {
            TrySyncSessionSection(
                executorCfgPath,
                targetCompId,
                senderCompId,
                targetSubId,
                senderSubId,
                BuildExecutorSessionLines(targetCompId, senderCompId, targetSubId, senderSubId),
                remove: true);
        }
    }

    private MappingConfig? LoadMappingForSessionSync(string filepath, string clientId)
    {
        try
        {
            return MappingConfig.Load(filepath);
        }
        catch
        {
            return null;
        }
    }

    private bool TrySyncSessionSection(
        string configPath,
        string senderCompId,
        string targetCompId,
        string? senderSubId,
        string? targetSubId,
        List<string> sessionLines,
        bool remove)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !System.IO.File.Exists(configPath))
        {
            _logger.LogWarning("FIX session config not found at {Path}", configPath);
            return false;
        }

        try
        {
            var updated = SyncSessionSection(
                configPath,
                senderCompId,
                targetCompId,
                senderSubId,
                targetSubId,
                sessionLines,
                remove,
                out var updatedLines);
            if (updated)
            {
                System.IO.File.WriteAllLines(configPath, updatedLines);
                _logger.LogInformation("Updated FIX session config at {Path}", configPath);
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update FIX session config at {Path}", configPath);
            return false;
        }
    }

    private static bool SyncSessionSection(
        string configPath,
        string senderCompId,
        string targetCompId,
        string? senderSubId,
        string? targetSubId,
        List<string> sessionLines,
        bool remove,
        out List<string> updatedLines)
    {
        updatedLines = new List<string>();

        var lines = System.IO.File.ReadAllLines(configPath);
        var preamble = new List<string>();
        var sections = new List<(string Name, List<string> Lines)>();
        string? currentName = null;
        var currentLines = new List<string>();
        var inSection = false;

        foreach (var line in lines)
        {
            if (IsSectionHeader(line, out var name))
            {
                if (inSection && currentName != null)
                {
                    sections.Add((currentName, currentLines));
                    currentLines = new List<string>();
                }

                currentName = name;
                inSection = true;
                continue;
            }

            if (!inSection)
            {
                preamble.Add(line);
            }
            else
            {
                currentLines.Add(line);
            }
        }

        if (inSection && currentName != null)
        {
            sections.Add((currentName, currentLines));
        }

        if (preamble.Count > 0)
        {
            updatedLines.AddRange(preamble);
        }

        var matched = false;
        var replaced = false;
        var desiredBeginString = GetConfigValue(sessionLines, "BeginString") ?? "FIX.4.2";

        foreach (var section in sections)
        {
            if (!string.Equals(section.Name, "SESSION", StringComparison.OrdinalIgnoreCase))
            {
                updatedLines.Add($"[{section.Name}]");
                updatedLines.AddRange(section.Lines);
                continue;
            }

            var existingSender = GetConfigValue(section.Lines, "SenderCompID");
            var existingTarget = GetConfigValue(section.Lines, "TargetCompID");
            var existingSenderSub = GetConfigValue(section.Lines, "SenderSubID");
            var existingTargetSub = GetConfigValue(section.Lines, "TargetSubID");
            var existingBeginString = GetConfigValue(section.Lines, "BeginString");

            if (!string.Equals(existingSender, senderCompId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingTarget, targetCompId, StringComparison.OrdinalIgnoreCase) ||
                !MatchesOptional(existingSenderSub, senderSubId) ||
                !MatchesOptional(existingTargetSub, targetSubId) ||
                (!string.IsNullOrWhiteSpace(existingBeginString) &&
                 !string.Equals(existingBeginString, desiredBeginString, StringComparison.OrdinalIgnoreCase)))
            {
                updatedLines.Add("[SESSION]");
                updatedLines.AddRange(section.Lines);
                continue;
            }

            matched = true;
            if (remove)
            {
                continue;
            }

            if (!replaced)
            {
                updatedLines.Add("[SESSION]");
                updatedLines.AddRange(sessionLines);
                replaced = true;
            }
        }

        if (!matched && !remove)
        {
            if (updatedLines.Count > 0 && !string.IsNullOrWhiteSpace(updatedLines.Last()))
            {
                updatedLines.Add(string.Empty);
            }

            updatedLines.Add("[SESSION]");
            updatedLines.AddRange(sessionLines);
        }

        return !lines.SequenceEqual(updatedLines);
    }

    private static bool IsSectionHeader(string line, out string name)
    {
        name = string.Empty;
        var trimmed = line?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        name = trimmed.Substring(1, trimmed.Length - 2);
        return !string.IsNullOrWhiteSpace(name);
    }

    private static string? GetConfigValue(IEnumerable<string> lines, string key)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var k = line.Substring(0, idx).Trim();
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line.Substring(idx + 1).Trim();
        }

        return null;
    }

    private static List<string> BuildWpfSessionLines(string senderCompId, string targetCompId, string? senderSubId, string? targetSubId)
    {
        var lines = new List<string>
        {
            "BeginString=FIX.4.2",
            "FileStorePath=store",
            $"SenderCompID={senderCompId}",
            $"TargetCompID={targetCompId}"
        };

        if (!string.IsNullOrWhiteSpace(senderSubId))
        {
            lines.Add($"SenderSubID={senderSubId}");
        }

        if (!string.IsNullOrWhiteSpace(targetSubId))
        {
            lines.Add($"TargetSubID={targetSubId}");
        }

        return lines;
    }

    private static List<string> BuildExecutorSessionLines(string senderCompId, string targetCompId, string? senderSubId, string? targetSubId)
    {
        var lines = new List<string>
        {
            "BeginString=FIX.4.2",
            $"SenderCompID={senderCompId}",
            $"TargetCompID={targetCompId}",
            "FileStorePath=store",
            "DataDictionary=../../spec/fix/FIX42.xml"
        };

        if (!string.IsNullOrWhiteSpace(senderSubId))
        {
            lines.Add($"SenderSubID={senderSubId}");
        }

        if (!string.IsNullOrWhiteSpace(targetSubId))
        {
            lines.Add($"TargetSubID={targetSubId}");
        }

        return lines;
    }

    private static bool TryDeployMappingCopy(string sourcePath, string destinationPath, out string? error)
    {
        error = null;
        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            System.IO.File.Copy(sourcePath, tempPath, overwrite: true);

            const int maxAttempts = 5;
            const int retryDelayMs = 200;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (System.IO.File.Exists(destinationPath))
                    {
                        System.IO.File.Replace(tempPath, destinationPath, null);
                    }
                    else
                    {
                        System.IO.File.Move(tempPath, destinationPath);
                    }

                    return true;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }

            error = "The destination mapping is in use. Close the email extraction service and try again.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(tempPath))
                {
                    System.IO.File.Delete(tempPath);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static string ResolveCliConfigsDir()
    {
        const string envVar = "FIXFLOW_CLI_CONFIGS";

        try
        {
            var env = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                var candidate = env;
                if (!Path.IsPathRooted(candidate))
                {
                    candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, candidate));
                }
                else
                {
                    candidate = Path.GetFullPath(candidate);
                }

                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // ignore and continue
        }

        var baseDir = AppContext.BaseDirectory;

        try
        {
            var solutionRoot = FindAncestorWithFile(baseDir, "*.sln", maxLevels: 8);
            if (!string.IsNullOrWhiteSpace(solutionRoot))
            {
                var expectedCliProj = Path.Combine(solutionRoot, "src", "FixFlow.TradeAllocBridge.CLI");
                if (Directory.Exists(expectedCliProj))
                {
                    var binDir = Path.Combine(expectedCliProj, "bin");
                    if (Directory.Exists(binDir))
                    {
                        foreach (var configuration in new[] { "Debug", "Release" })
                        {
                            var cfgDir = Path.Combine(binDir, configuration);
                            if (!Directory.Exists(cfgDir))
                            {
                                continue;
                            }

                            foreach (var tfmDir in Directory.GetDirectories(cfgDir))
                            {
                                var configsDir = Path.Combine(tfmDir, "configs");
                                if (Directory.Exists(configsDir))
                                {
                                    return Path.GetFullPath(configsDir);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // continue to other strategies
        }

        try
        {
            var candidate = Path.Combine(baseDir, "configs");
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "configs"));
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        catch
        {
            // continue
        }

        try
        {
            var solutionRoot = FindAncestorWithFile(baseDir, "*.sln", maxLevels: 8);
            if (!string.IsNullOrWhiteSpace(solutionRoot))
            {
                var srcDir = Path.Combine(solutionRoot, "src");
                if (Directory.Exists(srcDir))
                {
                    var cliProjects = Directory.GetDirectories(srcDir, "*CLI*", SearchOption.TopDirectoryOnly);
                    foreach (var proj in cliProjects)
                    {
                        var binDir = Path.Combine(proj, "bin");
                        if (!Directory.Exists(binDir))
                        {
                            continue;
                        }

                        foreach (var configuration in new[] { "Debug", "Release" })
                        {
                            var cfgDir = Path.Combine(binDir, configuration);
                            if (!Directory.Exists(cfgDir))
                            {
                                continue;
                            }

                            foreach (var tfmDir in Directory.GetDirectories(cfgDir))
                            {
                                var configsDir = Path.Combine(tfmDir, "configs");
                                if (Directory.Exists(configsDir))
                                {
                                    return Path.GetFullPath(configsDir);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var di = new DirectoryInfo(baseDir);
            for (int depth = 0; depth < 8 && di != null; depth++, di = di.Parent)
            {
                var found = di.GetDirectories("configs", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (found != null)
                {
                    return found.FullName;
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FixFlow", "configs");
            Directory.CreateDirectory(appData);
            return appData;
        }
        catch
        {
            return baseDir;
        }
    }

    private static string? ResolveCliFixCfgPath(string cliConfigsDir)
    {
        if (!string.IsNullOrWhiteSpace(cliConfigsDir) && Directory.Exists(cliConfigsDir))
        {
            var cliBaseDir = Directory.GetParent(cliConfigsDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(cliBaseDir))
            {
                var cfgPath = Path.Combine(cliBaseDir, "cfg", "FIX42.cfg");
                if (System.IO.File.Exists(cfgPath))
                {
                    return cfgPath;
                }
            }
        }

        return null;
    }

    private static string? ResolveExecutorCfgPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var solutionRoot = FindAncestorWithFile(baseDir, "*.sln", maxLevels: 8);
        if (string.IsNullOrWhiteSpace(solutionRoot))
        {
            return null;
        }

        var executorCfg = Path.Combine(solutionRoot, "quickfixn", "Examples", "Executor", "executor.cfg");
        return System.IO.File.Exists(executorCfg) ? executorCfg : null;
    }

    private static string? FindAncestorWithFile(string startPath, string searchPattern, int maxLevels)
    {
        try
        {
            var di = new DirectoryInfo(startPath);
            for (int i = 0; i < maxLevels && di != null; i++)
            {
                if (Directory.GetFiles(di.FullName, searchPattern).Any())
                {
                    return di.FullName;
                }

                di = di.Parent;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private sealed record FixTagCache(
        Dictionary<string, string> TagNames,
        Dictionary<string, string> TagLevels);
}
