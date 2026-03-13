using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Fix;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly ILogger<SettingsController> _logger;
    private readonly FixEngine _fixEngine;
    private readonly FixApp _fixApp;

    public SettingsController(
        ILogger<SettingsController> logger,
        FixEngine fixEngine,
        FixApp fixApp)
    {
        _logger = logger;
        _fixEngine = fixEngine;
        _fixApp = fixApp;
    }

    [HttpGet]
    public ActionResult<SettingsLoadResponse> Get()
    {
        var (settings, settingsStatus, settingsPath) = LoadSettings();
        var (fixDefaults, fixStatus, fixPath) = LoadFixDefaults();
        return Ok(new SettingsLoadResponse(
            settingsStatus,
            settings,
            fixStatus,
            fixDefaults,
            GetLastModified(settingsPath),
            GetLastModified(fixPath)));
    }

    [HttpPost("save")]
    public ActionResult<SettingsSaveResponse> Save([FromBody] SettingsSaveRequest request)
    {
        if (request is null)
        {
            return Ok(new SettingsSaveResponse(false, "Invalid request.", "Invalid request."));
        }

        var saveAppSettings = request.SaveAppSettings;
        var saveFixDefaults = request.SaveFixDefaults;

        var settingsResult = saveAppSettings
            ? SaveSettings(request.Settings ?? Array.Empty<SettingsUpdateItem>())
            : (Success: true, StatusMessage: "No app settings changes.");

        var fixResult = saveFixDefaults
            ? SaveFixDefaults(request.FixDefaults ?? Array.Empty<FixDefaultItemDto>())
            : (Success: true, StatusMessage: "No FIX default changes.");

        var success = (!saveAppSettings || settingsResult.Success) && (!saveFixDefaults || fixResult.Success);

        return Ok(new SettingsSaveResponse(success, settingsResult.StatusMessage, fixResult.StatusMessage));
    }

    private (IReadOnlyList<AppSettingItemDto> Settings, string StatusMessage, string? SettingsPath) LoadSettings()
    {
        var settingsPath = ResolveAppSettingsPath();
        if (string.IsNullOrWhiteSpace(settingsPath) || !System.IO.File.Exists(settingsPath))
        {
            return (Array.Empty<AppSettingItemDto>(), "appsettings.json not found.", settingsPath);
        }

        try
        {
            var json = System.IO.File.ReadAllText(settingsPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null)
            {
                return (Array.Empty<AppSettingItemDto>(), "Failed to parse appsettings.json.", settingsPath);
            }

            var items = new List<AppSettingItemDto>();
            foreach (var section in root)
            {
                if (section.Value is null)
                {
                    continue;
                }

                AddSettings(items, section.Key, section.Value, string.Empty);
            }

            return (items, $"Loaded {items.Count} app setting(s).", settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load app settings.");
            return (Array.Empty<AppSettingItemDto>(), $"Error loading app settings: {ex.Message}", settingsPath);
        }
    }

    private (bool Success, string StatusMessage) SaveSettings(IReadOnlyList<SettingsUpdateItem> settings)
    {
        var settingsPath = ResolveAppSettingsPath();
        if (string.IsNullOrWhiteSpace(settingsPath) || !System.IO.File.Exists(settingsPath))
        {
            return (false, "app settings not found.");
        }

        try
        {
            var json = System.IO.File.ReadAllText(settingsPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null)
            {
                return (false, "Failed to parse appsettings.json.");
            }

            var errors = new List<string>();
            foreach (var setting in settings)
            {
                if (setting.IsSecret && !setting.HasNewSecret)
                {
                    continue;
                }

                if (!SetJsonValue(root, setting.Path, setting.Value, errors))
                {
                    errors.Add(setting.Path);
                }
            }

            if (errors.Count > 0)
            {
                return (false, "Some values were invalid and not saved: " + string.Join(", ", errors.Distinct()));
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            System.IO.File.WriteAllText(settingsPath, root.ToJsonString(options));
            return (true, "App settings saved.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save app settings.");
            return (false, $"Error saving app settings: {ex.Message}");
        }
    }

    private (IReadOnlyList<FixDefaultItemDto> Defaults, string StatusMessage, string? FixPath) LoadFixDefaults()
    {
        var cfgPath = ResolveFixDefaultsPath();
        if (string.IsNullOrWhiteSpace(cfgPath) || !System.IO.File.Exists(cfgPath))
        {
            return (Array.Empty<FixDefaultItemDto>(), "FIX42.cfg not found.", cfgPath);
        }

        try
        {
            var lines = System.IO.File.ReadAllLines(cfgPath);
            if (!TryGetDefaultSectionRange(lines, out var start, out var end))
            {
                return (Array.Empty<FixDefaultItemDto>(), "FIX42.cfg [DEFAULT] section not found.", cfgPath);
            }

            var defaults = new List<FixDefaultItemDto>();
            for (var i = start + 1; i < end; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                var idx = line.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                defaults.Add(new FixDefaultItemDto(key, value));
            }

            return (defaults, $"Loaded {defaults.Count} FIX default(s).", cfgPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load FIX42.cfg defaults.");
            return (Array.Empty<FixDefaultItemDto>(), $"Error loading FIX defaults: {ex.Message}", cfgPath);
        }
    }

    private (bool Success, string StatusMessage) SaveFixDefaults(IReadOnlyList<FixDefaultItemDto> defaults)
    {
        var cfgPath = ResolveFixDefaultsPath();
        if (string.IsNullOrWhiteSpace(cfgPath) || !System.IO.File.Exists(cfgPath))
        {
            return (false, "FIX42.cfg not found.");
        }

        try
        {
            var lines = System.IO.File.ReadAllLines(cfgPath).ToList();

            if (!TryGetDefaultSectionRange(lines, out var start, out var end))
            {
                var newLines = new List<string> { "[DEFAULT]" };
                foreach (var item in defaults)
                {
                    newLines.Add($"{item.Key}={item.Value}");
                }
                newLines.Add(string.Empty);
                newLines.AddRange(lines);
                lines = newLines;
                start = 0;
                end = defaults.Count + 1;
            }

            var updatedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = start + 1; i < end; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                var idx = line.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, idx).Trim();
                var item = defaults.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    continue;
                }

                lines[i] = $"{key}={item.Value}";
                updatedKeys.Add(item.Key);
            }

            var missing = defaults
                .Where(item => !updatedKeys.Contains(item.Key))
                .Select(item => $"{item.Key}={item.Value}")
                .ToList();

            if (missing.Count > 0)
            {
                lines.InsertRange(end, missing);
            }

            System.IO.File.WriteAllLines(cfgPath, lines);
            _fixEngine.ReloadSettings(_fixApp);

            return (true, "FIX defaults saved and applied.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save FIX42.cfg defaults.");
            return (false, $"Error saving FIX defaults: {ex.Message}");
        }
    }

    private static void AddSettings(
        ICollection<AppSettingItemDto> items,
        string section,
        JsonNode node,
        string prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (property.Value is null) continue;
                    var nextPrefix = string.IsNullOrEmpty(prefix) ? property.Key : $"{prefix}:{property.Key}";
                    AddSettings(items, section, property.Value, nextPrefix);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (child is null) continue;
                    var nextPrefix = $"{prefix}[{i}]";
                    AddSettings(items, section, child, nextPrefix);
                }
                break;
            case JsonValue value:
                var key = string.IsNullOrEmpty(prefix) ? section : prefix;
                var path = string.IsNullOrEmpty(prefix) ? section : $"{section}:{prefix}";
                var displayKey = GetDisplayKey(section, key);
                var rawValue = GetValueString(value);
                var isSecret = key.IndexOf("ClientSecret", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("ClientSecret", StringComparison.OrdinalIgnoreCase) >= 0;
                var displayValue = isSecret
                    ? (string.IsNullOrWhiteSpace(rawValue) ? string.Empty : "********")
                    : rawValue;
                var actualValue = isSecret ? string.Empty : rawValue;
                items.Add(new AppSettingItemDto(section, displayKey, displayValue, path, isSecret, actualValue));
                break;
        }
    }

    private static string GetValueString(JsonValue value)
    {
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (value.TryGetValue<int>(out var i)) return i.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        return value.ToString();
    }

    private static string GetDisplayKey(string section, string key)
    {
        if (!string.Equals(section, "Fix", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(section, "FixSessionQualifiers", StringComparison.OrdinalIgnoreCase))
            {
                return key switch
                {
                    "Web" => "Web Session Qualifier",
                    "Client" => "WPF Session Qualifier",
                    "Service" => "Service Session Qualifier",
                    _ => key
                };
            }

            return key;
        }

        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tag))
        {
            return key;
        }

        return tag switch
        {
            49 => "SenderCompID (49)",
            56 => "TargetCompID (56)",
            57 => "TargetSubID (57)",
            115 => "OnBehalfOfCompID (115)",
            128 => "DeliverToCompID (128)",
            _ => key
        };
    }

    private static bool SetJsonValue(JsonObject root, string path, string value, List<string> errors)
    {
        var tokens = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        JsonNode? current = root;
        for (var i = 0; i < tokens.Length; i++)
        {
            var (name, index) = ParseSegment(tokens[i]);
            var isLast = i == tokens.Length - 1;

            if (current is not JsonObject obj)
            {
                return false;
            }

            obj.TryGetPropertyValue(name, out var next);

            if (index.HasValue)
            {
                var array = next as JsonArray;
                var createdArray = false;
                if (array is null)
                {
                    array = new JsonArray();
                    createdArray = true;
                }

                while (array.Count <= index.Value)
                {
                    array.Add(null);
                }

                if (isLast)
                {
                    var existing = array[index.Value];
                    var newValue = CreateJsonValue(value, existing, errors);
                    if (newValue is null) return false;
                    array[index.Value] = newValue;
                }
                else
                {
                    var child = array[index.Value] as JsonObject;
                    if (child is null)
                    {
                        child = new JsonObject();
                        array[index.Value] = child;
                    }

                    current = child;
                }

                if (createdArray)
                {
                    obj[name] = array;
                }
            }
            else
            {
                if (isLast)
                {
                    var existing = next;
                    var newValue = CreateJsonValue(value, existing, errors);
                    if (newValue is null) return false;
                    obj[name] = newValue;
                }
                else
                {
                    var child = next as JsonObject;
                    if (child is null)
                    {
                        child = new JsonObject();
                        obj[name] = child;
                    }

                    current = child;
                }
            }
        }

        return true;
    }

    private static (string Name, int? Index) ParseSegment(string segment)
    {
        var open = segment.IndexOf('[', StringComparison.Ordinal);
        if (open < 0) return (segment, null);

        var close = segment.IndexOf(']', open + 1);
        if (close < 0) return (segment, null);

        var name = segment.Substring(0, open);
        var indexText = segment.Substring(open + 1, close - open - 1);
        if (int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return (name, index);
        }

        return (segment, null);
    }

    private static JsonNode? CreateJsonValue(string value, JsonNode? existing, List<string> errors)
    {
        if (existing is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out _))
            {
                if (bool.TryParse(value, out var b))
                {
                    return JsonValue.Create(b);
                }

                errors.Add($"Invalid boolean: {value}");
                return null;
            }

            if (jsonValue.TryGetValue<int>(out _))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    return JsonValue.Create(i);
                }

                errors.Add($"Invalid number: {value}");
                return null;
            }

            if (jsonValue.TryGetValue<long>(out _))
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    return JsonValue.Create(l);
                }

                errors.Add($"Invalid number: {value}");
                return null;
            }

            if (jsonValue.TryGetValue<double>(out _))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return JsonValue.Create(d);
                }

                errors.Add($"Invalid number: {value}");
                return null;
            }
        }

        return JsonValue.Create(value);
    }

    private static string? ResolveAppSettingsPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var sharedPath = SharedConfigResolver.ResolveSharedAppSettingsPath(baseDir);
        if (!string.IsNullOrWhiteSpace(sharedPath))
        {
            return sharedPath;
        }

        var localAppSettings = Path.Combine(baseDir, "appsettings.json");
        if (System.IO.File.Exists(localAppSettings))
        {
            return localAppSettings;
        }

        var solutionRoot = FindAncestorWithFile(baseDir, "*.sln", maxLevels: 8);
        if (string.IsNullOrWhiteSpace(solutionRoot))
        {
            return null;
        }

        var rootAppSettings = Path.Combine(solutionRoot, "appsettings.json");
        return System.IO.File.Exists(rootAppSettings) ? rootAppSettings : null;
    }

    private static string? ResolveFixDefaultsPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var cfgPath = Path.Combine(baseDir, "cfg", "FIX42.cfg");
        return System.IO.File.Exists(cfgPath) ? cfgPath : null;
    }

    private static string? GetLastModified(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.LastWriteTime.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetDefaultSectionRange(IReadOnlyList<string> lines, out int start, out int end)
    {
        start = -1;
        end = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.EndsWith("]", StringComparison.Ordinal) &&
                string.Equals(line.Substring(1, line.Length - 2), "DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return false;
        }

        end = lines.Count;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        return true;
    }

    private static string? FindAncestorWithFile(string startPath, string searchPattern, int maxLevels)
    {
        try
        {
            var di = new DirectoryInfo(startPath);
            for (int i = 0; i < maxLevels && di != null; i++)
            {
                if (Directory.GetFiles(di.FullName, searchPattern).Any()) return di.FullName;
                di = di.Parent;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
