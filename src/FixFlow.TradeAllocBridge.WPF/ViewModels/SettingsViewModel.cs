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

namespace FixFlow.TradeAllocBridge.WPF.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private readonly ILogger<SettingsViewModel> _logger;
        private string _statusMessage = "Ready";
        private string? _settingsFilePath;

        public SettingsViewModel(ILogger<SettingsViewModel> logger)
        {
            _logger = logger;
            Settings = new ObservableCollection<SettingItem>();
            ReloadCommand = new RelayCommand(LoadSettings);
            SaveCommand = new RelayCommand(SaveSettings, () => Settings.Count > 0);

            LoadSettings();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<SettingItem> Settings { get; }

        public ICommand ReloadCommand { get; }
        public ICommand SaveCommand { get; }

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

                StatusMessage = $"Loaded {Settings.Count} setting(s).";
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load appsettings.json.");
                StatusMessage = $"Error loading settings: {ex.Message}";
            }
        }

        private void SaveSettings()
        {
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
                foreach (var setting in Settings)
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
                foreach (var setting in Settings)
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
            var solutionRoot = FindAncestorWithFile(baseDir, "*.sln", maxLevels: 8);
            if (string.IsNullOrWhiteSpace(solutionRoot))
            {
                return null;
            }

            var wpfAppSettings = Path.Combine(solutionRoot, "src", "FixFlow.TradeAllocBridge.WPF", "appsettings.json");
            return File.Exists(wpfAppSettings) ? wpfAppSettings : null;
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

        public SettingItem(string section, string key, string displayKey, string value, string path, bool isSecret)
        {
            Section = section;
            Key = key;
            DisplayKey = displayKey;
            _value = value;
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
                }
            }
        }

        public bool IsSecret { get; }

        public bool HasNewSecret { get; private set; }

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
