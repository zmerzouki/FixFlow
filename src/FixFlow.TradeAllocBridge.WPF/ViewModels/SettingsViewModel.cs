using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Fix;

namespace FixFlow.TradeAllocBridge.WPF.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private readonly ILogger<SettingsViewModel> _logger;
        private readonly FixEngine _fixEngine;
        private readonly FixApp _fixApp;
        private string _statusMessage = "Ready";
        private string _fixDefaultsStatusMessage = "Ready";
        private string? _settingsFilePath;
        private string? _fixDefaultsFilePath;

        public SettingsViewModel(ILogger<SettingsViewModel> logger, FixEngine fixEngine, FixApp fixApp)
        {
            _logger = logger;
            _fixEngine = fixEngine;
            _fixApp = fixApp;
            Settings = new ObservableCollection<SettingItem>();
            FixDefaults = new ObservableCollection<FixDefaultSettingItem>();
            ReloadCommand = new RelayCommand(ReloadAll);
            SaveCommand = new RelayCommand(SaveAll, () => HasSettingsChanges || HasFixDefaultsChanges);
            ReloadFixDefaultsCommand = new RelayCommand(LoadFixDefaults);
            SaveFixDefaultsCommand = new RelayCommand(SaveFixDefaults, () => FixDefaults.Count > 0);

            LoadSettings();
            LoadFixDefaults();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<SettingItem> Settings { get; }
        public ObservableCollection<FixDefaultSettingItem> FixDefaults { get; }

        public ICommand ReloadCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ReloadFixDefaultsCommand { get; }
        public ICommand SaveFixDefaultsCommand { get; }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged(nameof(StatusMessage));
                }
            }
        }

        public string FixDefaultsStatusMessage
        {
            get => _fixDefaultsStatusMessage;
            set
            {
                if (_fixDefaultsStatusMessage != value)
                {
                    _fixDefaultsStatusMessage = value;
                    OnPropertyChanged(nameof(FixDefaultsStatusMessage));
                }
            }
        }

        public void LoadSettings()
        {
            Settings.Clear();
            _settingsFilePath = ResolveWpfAppSettingsPath();

            if (string.IsNullOrWhiteSpace(_settingsFilePath) || !File.Exists(_settingsFilePath))
            {
                StatusMessage = "appsettings.json not found in the WPF project.";
                return;
            }

            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root is null)
                {
                    StatusMessage = "Failed to parse appsettings.json.";
                    return;
                }

                foreach (var section in root)
                {
                    if (section.Value is null) continue;
                    AddSettings(section.Key, section.Value, string.Empty);
                }

                foreach (var item in Settings)
                {
                    item.PropertyChanged += SettingItem_OnPropertyChanged;
                }

                StatusMessage = $"Loaded {Settings.Count} setting(s).";
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load appsettings.json.");
                StatusMessage = $"Error loading settings: {ex.Message}";
            }
        }

        private void ReloadAll()
        {
            LoadSettings();
            LoadFixDefaults();
        }

        public void LoadFixDefaults()
        {
            FixDefaults.Clear();
            _fixDefaultsFilePath = ResolveFixDefaultsPath();

            if (string.IsNullOrWhiteSpace(_fixDefaultsFilePath) || !File.Exists(_fixDefaultsFilePath))
            {
                FixDefaultsStatusMessage = "FIX42.cfg not found.";
                return;
            }

            try
            {
                var lines = File.ReadAllLines(_fixDefaultsFilePath);
                if (!TryGetDefaultSectionRange(lines, out var start, out var end))
                {
                    FixDefaultsStatusMessage = "FIX42.cfg [DEFAULT] section not found.";
                    return;
                }

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

                    var item = new FixDefaultSettingItem(key, value);
                    item.PropertyChanged += FixDefault_OnPropertyChanged;
                    FixDefaults.Add(item);
                }

                FixDefaultsStatusMessage = $"Loaded {FixDefaults.Count} FIX default(s).";
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load FIX42.cfg defaults.");
                FixDefaultsStatusMessage = $"Error loading FIX defaults: {ex.Message}";
            }
        }

        private void SaveSettings()
        {
            if (!HasSettingsChanges)
            {
                StatusMessage = "No app settings changes.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_settingsFilePath) || !File.Exists(_settingsFilePath))
            {
                StatusMessage = "appsettings.json not found in the WPF project.";
                return;
            }

            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root is null)
                {
                    StatusMessage = "Failed to parse appsettings.json.";
                    return;
                }

                var errors = new List<string>();
                foreach (var setting in Settings.Where(s => s.IsDirty))
                {
                    if (setting.IsSecret && !setting.HasNewSecret)
                    {
                        continue;
                    }

                    var updated = SetJsonValue(root, setting.Path, setting.Value, errors);
                    if (!updated)
                    {
                        errors.Add($"{setting.Section}:{setting.Key}");
                    }
                }

                if (errors.Count > 0)
                {
                    StatusMessage = "Some values were invalid and not saved: " + string.Join(", ", errors.Distinct());
                    return;
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_settingsFilePath, root.ToJsonString(options));
                foreach (var setting in Settings.Where(s => s.IsDirty))
                {
                    setting.MarkSaved();
                }
                StatusMessage = "Settings saved.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save appsettings.json.");
                StatusMessage = $"Error saving settings: {ex.Message}";
            }
        }

        private void SaveAll()
        {
            var savedAnything = false;

            if (HasSettingsChanges)
            {
                SaveSettings();
                savedAnything = true;
            }
            else
            {
                StatusMessage = "No app settings changes.";
            }

            if (HasFixDefaultsChanges)
            {
                SaveFixDefaults();
                savedAnything = true;
            }
            else
            {
                FixDefaultsStatusMessage = "No FIX default changes.";
            }

            if (!savedAnything)
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void SaveFixDefaults()
        {
            if (!HasFixDefaultsChanges)
            {
                FixDefaultsStatusMessage = "No FIX default changes.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_fixDefaultsFilePath) || !File.Exists(_fixDefaultsFilePath))
            {
                FixDefaultsStatusMessage = "FIX42.cfg not found.";
                return;
            }

            try
            {
                var lines = File.ReadAllLines(_fixDefaultsFilePath).ToList();

                if (!TryGetDefaultSectionRange(lines, out var start, out var end))
                {
                    var newLines = new List<string> { "[DEFAULT]" };
                    foreach (var item in FixDefaults)
                    {
                        newLines.Add($"{item.Key}={item.Value}");
                    }
                    newLines.Add(string.Empty);
                    newLines.AddRange(lines);
                    lines = newLines;
                    start = 0;
                    end = FixDefaults.Count + 1;
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
                    var item = FixDefaults.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        continue;
                    }

                    lines[i] = $"{key}={item.Value}";
                    updatedKeys.Add(item.Key);
                }

                var missing = FixDefaults
                    .Where(item => !updatedKeys.Contains(item.Key))
                    .Select(item => $"{item.Key}={item.Value}")
                    .ToList();

                if (missing.Count > 0)
                {
                    lines.InsertRange(end, missing);
                }

                File.WriteAllLines(_fixDefaultsFilePath, lines);
                _fixEngine.ReloadSettings(_fixApp);

                foreach (var item in FixDefaults.Where(x => x.IsDirty))
                {
                    item.MarkSaved();
                }

                FixDefaultsStatusMessage = "FIX defaults saved and applied.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save FIX42.cfg defaults.");
                FixDefaultsStatusMessage = $"Error saving FIX defaults: {ex.Message}";
            }
        }

        private void AddSettings(string section, JsonNode node, string prefix)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        if (property.Value is null) continue;
                        var nextPrefix = string.IsNullOrEmpty(prefix) ? property.Key : $"{prefix}:{property.Key}";
                        AddSettings(section, property.Value, nextPrefix);
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        var child = array[i];
                        if (child is null) continue;
                        var nextPrefix = $"{prefix}[{i}]";
                        AddSettings(section, child, nextPrefix);
                    }
                    break;
                case JsonValue value:
                    var key = string.IsNullOrEmpty(prefix) ? section : prefix;
                    var path = string.IsNullOrEmpty(prefix) ? section : $"{section}:{prefix}";
                    var displayKey = GetDisplayKey(section, key);
                    var rawValue = GetValueString(value);
                    var isSecret = key.IndexOf("ClientSecret", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("ClientSecret", StringComparison.OrdinalIgnoreCase) >= 0;
                    var item = new SettingItem(section, key, displayKey, rawValue, path, isSecret);
                    Settings.Add(item);
                    break;
            }
        }

        private bool HasSettingsChanges => Settings.Any(item => item.IsDirty);

        private bool HasFixDefaultsChanges => FixDefaults.Any(item => item.IsDirty);

        private void SettingItem_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingItem.Value) ||
                e.PropertyName == nameof(SettingItem.HasNewSecret) ||
                e.PropertyName == nameof(SettingItem.IsDirty))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void FixDefault_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FixDefaultSettingItem.Value) ||
                e.PropertyName == nameof(FixDefaultSettingItem.IsDirty))
            {
                CommandManager.InvalidateRequerySuggested();
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

        private static string? ResolveWpfAppSettingsPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var sharedPath = SharedConfigResolver.ResolveSharedAppSettingsPath(baseDir);
            if (!string.IsNullOrWhiteSpace(sharedPath))
            {
                return sharedPath;
            }

            var localAppSettings = Path.Combine(baseDir, "appsettings.json");
            if (File.Exists(localAppSettings))
            {
                return localAppSettings;
            }

            var solutionRoot = FindAncestorWithFile(baseDir, "*.sln", maxLevels: 8);
            if (string.IsNullOrWhiteSpace(solutionRoot))
            {
                return null;
            }

            var wpfAppSettings = Path.Combine(solutionRoot, "src", "FixFlow.TradeAllocBridge.WPF", "appsettings.json");
            return File.Exists(wpfAppSettings) ? wpfAppSettings : null;
        }

        private static string? ResolveFixDefaultsPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var cfgPath = Path.Combine(baseDir, "cfg", "FIX42.cfg");
            return File.Exists(cfgPath) ? cfgPath : null;
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
            catch { }

            return null;
        }

        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SettingItem : INotifyPropertyChanged
    {
        private string _value;
        private string _originalValue;

        public SettingItem(string section, string key, string displayKey, string value, string path, bool isSecret)
        {
            Section = section;
            Key = key;
            DisplayKey = displayKey;
            _value = value;
            _originalValue = value;
            Path = path;
            IsSecret = isSecret;
        }

        public string Section { get; }
        public string Key { get; }
        public string DisplayKey { get; }
        public string Path { get; }

        public string Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
                }
            }
        }

        public string OriginalValue
        {
            get => _originalValue;
            private set => _originalValue = value;
        }

        public bool IsSecret { get; }

        public bool HasNewSecret { get; private set; }

        public bool IsDirty => IsSecret
            ? HasNewSecret
            : !string.Equals(_value, _originalValue, StringComparison.Ordinal);

        public string DisplayValue => IsSecret
            ? (string.IsNullOrWhiteSpace(_value) ? string.Empty : "********")
            : _value;

        public string EditValue
        {
            get => IsSecret ? string.Empty : _value;
            set
            {
                if (IsSecret)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _value = value;
                        HasNewSecret = true;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNewSecret)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
                    }
                }
                else
                {
                    Value = value;
                }
            }
        }

        public void MarkSaved()
        {
            HasNewSecret = false;
            OriginalValue = _value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class FixDefaultSettingItem : INotifyPropertyChanged
    {
        private string _value;
        private string _originalValue;

        public FixDefaultSettingItem(string key, string value)
        {
            Key = key;
            _value = value;
            _originalValue = value;
        }

        public string Key { get; }

        public string Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
                }
            }
        }

        public string OriginalValue
        {
            get => _originalValue;
            private set => _originalValue = value;
        }

        public bool IsDirty => !string.Equals(_value, _originalValue, StringComparison.Ordinal);

        public void MarkSaved()
        {
            OriginalValue = _value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
