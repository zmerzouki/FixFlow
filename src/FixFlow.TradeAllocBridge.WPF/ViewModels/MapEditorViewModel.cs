using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Mapping;
using System.ComponentModel;

namespace FixFlow.TradeAllocBridge.WPF.ViewModels
{
    public class MapEditorViewModel : INotifyPropertyChanged
    {
        private readonly FixMappingRepository _mappingRepo;
        private readonly ILogger<MapEditorViewModel> _logger;
        private readonly string _configDir;
        private readonly string _cliConfigsDir;

        private MappingConfig? _currentMapping;
        private KeyValuePair<string, string>? _selectedFieldMapping;
        private string _senderCompId = string.Empty;
        private string _targetCompId = string.Empty;
        private string _targetSubId = string.Empty;
        private string _onBehalfOfCompId = string.Empty;
        private string _deliverToCompId = string.Empty;
        private string _newFieldName = string.Empty;
        private string _newFieldTag = string.Empty;
        private bool _isProgrammaticSenderCompSet;
        private bool _isEditing;
        private string _statusMessage = "Ready";
        private int _testTradesCount;
        private ObservableCollection<FixMessageResult> _testResults;
        private bool _hasUnsavedChanges;
        private bool _suppressChangeTracking;
        private string _incomingFolderPath = string.Empty;
        private const string DefaultClientIdPlaceholder = "CLIENT1";
        private const string DefaultSenderDomainPlaceholder = "ACME.COM";
        private const string DefaultSenderCompIdPlaceholder = "FIXFLOW";
        private const string DefaultTargetCompIdPlaceholder = "BROKER";
        private static readonly string[] RequiredFixTags = { "54", "55", "79", "80", "153" };

        // Available FIX tags for dropdowns
        public readonly Dictionary<int, string> CommonFixTags = new()
        {
            { 1, "Account" },
            { 6, "AvgPx" },
            { 11, "ClOrdID" },
            { 14, "CumQty" },
            { 38, "OrderQty" },
            { 40, "OrdType" },
            { 44, "Price" },
            { 49, "SenderCompID" },
            { 54, "Side (Buy=1, Sell=2)" },
            { 55, "Symbol" },
            { 56, "TargetCompID" },
            { 57, "TargetSubID" },
            { 70, "AllocID" },
            { 71, "AllocTransType" },
            { 75, "TradeDate" },
            { 76, "ExecRefID" },
            { 78, "NoAllocs" },
            { 79, "AllocAccount" },
            { 80, "AllocQty" },
            { 87, "AllocStatus" },
            { 100, "ExDestination" },
            { 115, "OnBehalfOfCompID" },
            { 128, "DeliverToCompID" },
            { 150, "ExecType" },
            { 153, "AllocPrice" },
            { 209, "XmlDataLen" }
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<string> AvailableMappings { get; }
        public ObservableCollection<KeyValuePair<string, string>> FieldMappings { get; }
        public ICommand NewMappingCommand { get; }
        public ICommand EditMappingCommand { get; }
        public ICommand SaveMappingCommand { get; }
        public ICommand DeleteMappingCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand AddFieldCommand { get; }
        public ICommand UpdateFieldCommand { get; }
        public ICommand RemoveFieldCommand { get; }
        public ICommand MoveFieldUpCommand { get; }
        public ICommand MoveFieldDownCommand { get; }
        public ICommand TestMappingCommand { get; }
        public ICommand DeployMappingCommand { get; }

        public MapEditorViewModel(FixMappingRepository mappingRepo, ILogger<MapEditorViewModel> logger)
        {
            _mappingRepo = mappingRepo;
            _logger = logger;
            _configDir = _mappingRepo.BaseDirectory;
            // Use resolver instead of fragile hardcoded relative path
            _cliConfigsDir = ResolveCliConfigsDir();
            _testResults = new ObservableCollection<FixMessageResult>();
            _hasUnsavedChanges = false;
            _suppressChangeTracking = false;

            AvailableMappings = new ObservableCollection<string>();
            FieldMappings = new ObservableCollection<KeyValuePair<string, string>>();

            NewMappingCommand = new RelayCommand(NewMapping);
            EditMappingCommand = new RelayCommand(EditMapping, () => SelectedMapping != null);
            SaveMappingCommand = new RelayCommand(SaveMapping, () => CanSave);
            DeleteMappingCommand = new RelayCommand(DeleteMapping, () => SelectedMapping != null && !IsEditing);
            CancelCommand = new RelayCommand(Cancel, () => IsEditing);
            AddFieldCommand = new RelayCommand(AddField, () => CanAddField);
            UpdateFieldCommand = new RelayCommand(UpdateField, () => CanUpdateField);
            RemoveFieldCommand = new RelayCommand(RemoveField, () => SelectedFieldMapping != null);
            MoveFieldUpCommand = new RelayCommand(MoveFieldUp, () => CanMoveFieldUp);
            MoveFieldDownCommand = new RelayCommand(MoveFieldDown, () => CanMoveFieldDown);
            TestMappingCommand = new RelayCommand(TestMapping, () => !IsEditing && SelectedMapping != null);
            DeployMappingCommand = new RelayCommand(DeployMapping, () => !IsEditing && SelectedMapping != null);

            LoadAvailableMappings();
        }

        public void LoadAvailableMappings()
        {
            AvailableMappings.Clear();
            try
            {
                var mappings = _mappingRepo.GetAll().OrderBy(m => m.ClientId).ToList();
                foreach (var mapping in mappings)
                {
                    AvailableMappings.Add(mapping.ClientId);
                }
                StatusMessage = $"Loaded {AvailableMappings.Count} mapping(s)";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading mappings: {ex.Message}";
                _logger.LogError(ex, "Failed to load mappings");
            }
        }

        private string? _selectedMapping;
        public string? SelectedMapping
        {
            get => _selectedMapping;
            set
            {
                if (_selectedMapping != value)
                {
                    _selectedMapping = value;
                    OnPropertyChanged(nameof(SelectedMapping));
                    if (!IsEditing && value != null)
                    {
                        LoadMappingDetails(value);
                    }
                }
            }
        }

        private void LoadMappingDetails(string clientId)
        {
            _suppressChangeTracking = true;
            try
            {
                var mapFile = Path.Combine(_configDir, $"{clientId}_map.json");
                if (File.Exists(mapFile))
                {
                    _currentMapping = MappingConfig.Load(mapFile);
                    _isProgrammaticSenderCompSet = true;
                    SenderCompId = _currentMapping.Predefined?.SenderCompID ?? string.Empty;
                    _isProgrammaticSenderCompSet = false;
                    TargetCompId = _currentMapping.Predefined?.TargetCompID ?? string.Empty;
                    TargetSubId = _currentMapping.Predefined?.TargetSubID ?? string.Empty;
                    OnBehalfOfCompId = _currentMapping.Predefined?.OnBehalfOfCompID ?? string.Empty;
                    DeliverToCompId = _currentMapping.Predefined?.DeliverToCompID ?? string.Empty;

                    FieldMappings.Clear();
                    foreach (var kvp in _currentMapping.TradeAllocations)
                    {
                        FieldMappings.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Value));
                    }

                    StatusMessage = $"Loaded mapping: {clientId}";
                    OnPropertyChanged(nameof(ClientId));
                    OnPropertyChanged(nameof(SenderDomain));
                    OnPropertyChanged(nameof(CanSave));
                    OnPropertyChanged(nameof(MissingRequiredTagsMessage));
                    _hasUnsavedChanges = false;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading mapping details: {ex.Message}";
                _logger.LogError(ex, "Failed to load mapping details for {ClientId}", clientId);
            }
            finally
            {
                _suppressChangeTracking = false;
            }
        }

        public string ClientId
        {
            get => _currentMapping?.ClientId ?? string.Empty;
            set
            {
                var upper = value?.ToUpperInvariant() ?? string.Empty;
                if (_currentMapping != null && _currentMapping.ClientId != upper)
                {
                    _currentMapping.ClientId = upper;
                    OnPropertyChanged(nameof(ClientId));
                    MarkDirty();
                }
            }
        }

        public void RefreshSelectedMapping()
        {
            if (!string.IsNullOrWhiteSpace(SelectedMapping))
            {
                LoadMappingDetails(SelectedMapping);
            }
        }

        public string SenderDomain
        {
            get => _currentMapping?.SenderDomain ?? string.Empty;
            set
            {
                var upper = value?.ToUpperInvariant() ?? string.Empty;
                if (_currentMapping != null && _currentMapping.SenderDomain != upper)
                {
                    _currentMapping.SenderDomain = upper;
                    OnPropertyChanged(nameof(SenderDomain));
                    MarkDirty();
                }
            }
        }

        public string SenderCompId
        {
            get => _senderCompId;
            set
            {
                var upper = value?.ToUpperInvariant() ?? string.Empty;
                if (_senderCompId != upper)
                {
                    _senderCompId = upper;
                    if (!_isProgrammaticSenderCompSet)
                    {
                        MarkDirty();
                    }
                    OnPropertyChanged(nameof(SenderCompId));
                }
            }
        }

        public string TargetCompId
        {
            get => _targetCompId;
            set
            {
                var upper = value?.ToUpperInvariant() ?? string.Empty;
                if (_targetCompId != upper)
                {
                    _targetCompId = upper;
                    OnPropertyChanged(nameof(TargetCompId));
                    MarkDirty();
                }
            }
        }

        public string TargetSubId
        {
            get => _targetSubId;
            set
            {
                var upper = value?.ToUpperInvariant() ?? string.Empty;
                if (_targetSubId != upper)
                {
                    _targetSubId = upper;
                    OnPropertyChanged(nameof(TargetSubId));
                    MarkDirty();
                }
            }
        }

        public string OnBehalfOfCompId
        {
            get => _onBehalfOfCompId;
            set
            {
                var upper = value?.ToUpperInvariant() ?? string.Empty;
                if (_onBehalfOfCompId != upper)
                {
                    _onBehalfOfCompId = upper;
                    OnPropertyChanged(nameof(OnBehalfOfCompId));
                    MarkDirty();
                }
            }
        }

        public string DeliverToCompId
        {
            get => _deliverToCompId;
            set
            {
                var upper = value?.ToUpperInvariant() ?? string.Empty;
                if (_deliverToCompId != upper)
                {
                    _deliverToCompId = upper;
                    OnPropertyChanged(nameof(DeliverToCompId));
                    MarkDirty();
                }
            }
        }

        public string ClientIdPlaceholder => DefaultClientIdPlaceholder;
        public string SenderDomainPlaceholder => DefaultSenderDomainPlaceholder;
        public string SenderCompIdPlaceholder => DefaultSenderCompIdPlaceholder;
        public string TargetCompIdPlaceholder => DefaultTargetCompIdPlaceholder;
        public string MissingRequiredTagsMessage
        {
            get
            {
                var missing = RequiredFixTags
                    .Where(tag => !FieldMappings.Any(f => string.Equals(f.Value?.Trim(), tag, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                return missing.Count == 0
                    ? string.Empty
                    : $"This map cannot be saved because it is missing the mapping for tag(s): {string.Join(", ", missing)}";
            }
        }

        public string IncomingFolderPath
        {
            get => _incomingFolderPath;
            set
            {
                if (_incomingFolderPath != value)
                {
                    _incomingFolderPath = value;
                    OnPropertyChanged(nameof(IncomingFolderPath));
                }
            }
        }

        public string NewFieldName
        {
            get => _newFieldName;
            set
            {
                if (_newFieldName != value)
                {
                    _newFieldName = value;
                    OnPropertyChanged(nameof(NewFieldName));
                    RefreshFieldCommands();
                }
            }
        }

        public string NewFieldTag
        {
            get => _newFieldTag;
            set
            {
                if (_newFieldTag != value)
                {
                    _newFieldTag = value;
                    OnPropertyChanged(nameof(NewFieldTag));
                    RefreshFieldCommands();
                }
            }
        }

        public KeyValuePair<string, string>? SelectedFieldMapping
        {
            get => _selectedFieldMapping;
            set
            {
                if ((!_selectedFieldMapping.HasValue && !value.HasValue) ||
                    (_selectedFieldMapping.HasValue && value.HasValue &&
                     _selectedFieldMapping.Value.Key == value.Value.Key &&
                     _selectedFieldMapping.Value.Value == value.Value.Value))
                {
                    return;
                }

                _selectedFieldMapping = value;
                if (value.HasValue)
                {
                    NewFieldName = value.Value.Key;
                    NewFieldTag = value.Value.Value;
                }
                OnPropertyChanged(nameof(SelectedFieldMapping));
                RefreshFieldCommands();
            }
        }

        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing != value)
                {
                    _isEditing = value;
                    OnPropertyChanged(nameof(IsEditing));
                    OnPropertyChanged(nameof(CanSave));
                    RefreshFieldCommands();
                }
            }
        }

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

        public bool CanUpdateField => IsEditing && SelectedFieldMapping.HasValue && IsFieldDirty && !string.IsNullOrWhiteSpace(NewFieldName) && !string.IsNullOrWhiteSpace(NewFieldTag);
        public bool CanAddField
        {
            get
            {
                if (string.IsNullOrWhiteSpace(NewFieldName) || string.IsNullOrWhiteSpace(NewFieldTag))
                {
                    return false;
                }

                if (SelectedFieldMapping.HasValue && !IsFieldDirty)
                {
                    return false;
                }

                return true;
            }
        }

        private bool IsFieldDirty => !SelectedFieldMapping.HasValue ||
            !string.Equals(SelectedFieldMapping.Value.Key, NewFieldName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SelectedFieldMapping.Value.Value, NewFieldTag, StringComparison.OrdinalIgnoreCase);

        private void RefreshFieldCommands()
        {
            OnPropertyChanged(nameof(CanAddField));
            OnPropertyChanged(nameof(CanUpdateField));
            OnPropertyChanged(nameof(CanMoveFieldUp));
            OnPropertyChanged(nameof(CanMoveFieldDown));
            CommandManager.InvalidateRequerySuggested();
        }

        private int GetSelectedFieldIndex()
        {
            if (!SelectedFieldMapping.HasValue)
            {
                return -1;
            }

            var selected = SelectedFieldMapping.Value;
            for (var i = 0; i < FieldMappings.Count; i++)
            {
                var item = FieldMappings[i];
                if (string.Equals(item.Key, selected.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Value, selected.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private void MoveFieldUp()
        {
            var index = GetSelectedFieldIndex();
            if (index <= 0) return;

            var item = FieldMappings[index];
            FieldMappings.RemoveAt(index);
            FieldMappings.Insert(index - 1, item);
            SelectedFieldMapping = FieldMappings[index - 1];
            MarkDirty();
            OnPropertyChanged(nameof(CanSave));
            CommandManager.InvalidateRequerySuggested();
        }

        private void MoveFieldDown()
        {
            var index = GetSelectedFieldIndex();
            if (index < 0 || index >= FieldMappings.Count - 1) return;

            var item = FieldMappings[index];
            FieldMappings.RemoveAt(index);
            FieldMappings.Insert(index + 1, item);
            SelectedFieldMapping = FieldMappings[index + 1];
            MarkDirty();
            OnPropertyChanged(nameof(CanSave));
            CommandManager.InvalidateRequerySuggested();
        }

        private bool HasRequiredTags => RequiredFixTags.All(tag =>
            FieldMappings.Any(f => string.Equals(f.Value?.Trim(), tag, StringComparison.OrdinalIgnoreCase)));

        public bool CanSave => IsEditing && !string.IsNullOrWhiteSpace(ClientId) && FieldMappings.Count > 0 && HasRequiredTags;

        public bool CanMoveFieldUp => IsEditing && GetSelectedFieldIndex() > 0;
        public bool CanMoveFieldDown => IsEditing && GetSelectedFieldIndex() >= 0 && GetSelectedFieldIndex() < FieldMappings.Count - 1;

        public ObservableCollection<FixMessageResult> TestResults
        {
            get => _testResults;
            set
            {
                if (_testResults != value)
                {
                    _testResults = value;
                    OnPropertyChanged(nameof(TestResults));
                }
            }
        }

        public int TestTradesCount
        {
            get => _testTradesCount;
            set
            {
                if (_testTradesCount != value)
                {
                    _testTradesCount = value;
                    OnPropertyChanged(nameof(TestTradesCount));
                }
            }
        }

        private void NewMapping()
        {
            _suppressChangeTracking = true;
            var wpfPredefinedDefaults = TryLoadWpfPredefinedDefaults();
            var defaultSenderCompId = wpfPredefinedDefaults?.SenderCompID ?? string.Empty;
            var defaultTargetCompId = wpfPredefinedDefaults?.TargetCompID ?? string.Empty;
            var defaultTargetSubId = wpfPredefinedDefaults?.TargetSubID ?? string.Empty;
            var defaultOnBehalfOfCompId = wpfPredefinedDefaults?.OnBehalfOfCompID ?? string.Empty;
            var defaultDeliverToCompId = wpfPredefinedDefaults?.DeliverToCompID ?? string.Empty;
            _currentMapping = new MappingConfig
            {
                ClientId = string.Empty,
                SenderDomain = string.Empty,
                Predefined = new PredefinedFields
                {
                    SenderCompID = defaultSenderCompId,
                    TargetCompID = defaultTargetCompId,
                    TargetSubID = string.IsNullOrWhiteSpace(defaultTargetSubId) ? null : defaultTargetSubId,
                    OnBehalfOfCompID = string.IsNullOrWhiteSpace(defaultOnBehalfOfCompId) ? null : defaultOnBehalfOfCompId,
                    DeliverToCompID = string.IsNullOrWhiteSpace(defaultDeliverToCompId) ? null : defaultDeliverToCompId
                },
                TradeAllocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            _isProgrammaticSenderCompSet = true;
            SenderCompId = defaultSenderCompId;
            _isProgrammaticSenderCompSet = false;
            TargetCompId = defaultTargetCompId;
            TargetSubId = defaultTargetSubId;
            OnBehalfOfCompId = defaultOnBehalfOfCompId;
            DeliverToCompId = defaultDeliverToCompId;
            FieldMappings.Clear();
            var defaults = new[]
            {
                new KeyValuePair<string, string>("Side", "54"),
                new KeyValuePair<string, string>("Symbol", "55"),
                new KeyValuePair<string, string>("Trade Date", "75"),
                new KeyValuePair<string, string>("Allocation Account", "79"),
                new KeyValuePair<string, string>("Shares Qty", "80"),
                new KeyValuePair<string, string>("Average Price", "153")
            };
            foreach (var kvp in defaults)
            {
                FieldMappings.Add(kvp);
                _currentMapping.TradeAllocations[kvp.Key] = kvp.Value;
            }
            OnPropertyChanged(nameof(MissingRequiredTagsMessage));
            NewFieldName = string.Empty;
            NewFieldTag = string.Empty;
            OnPropertyChanged(nameof(CanSave));
            CommandManager.InvalidateRequerySuggested();
            OnPropertyChanged(nameof(ClientId));
            OnPropertyChanged(nameof(SenderDomain));
            OnPropertyChanged(nameof(SenderCompId));
            OnPropertyChanged(nameof(TargetCompId));

            IsEditing = true;
            _suppressChangeTracking = false;
            _hasUnsavedChanges = true;
            StatusMessage = "Creating new mapping...";
        }

        private PredefinedFields? TryLoadWpfPredefinedDefaults()
        {
            try
            {
                var appSettingsPath = ResolveWpfAppSettingsPath();
                if (string.IsNullOrWhiteSpace(appSettingsPath) || !File.Exists(appSettingsPath))
                {
                    return null;
                }

                var json = File.ReadAllText(appSettingsPath);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Fix", out var fixElement) ||
                    fixElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var predefined = new PredefinedFields
                {
                    SenderCompID = GetFixSetting(fixElement, "49"),
                    TargetCompID = GetFixSetting(fixElement, "56"),
                    TargetSubID = GetFixSetting(fixElement, "57"),
                    OnBehalfOfCompID = GetFixSetting(fixElement, "115"),
                    DeliverToCompID = GetFixSetting(fixElement, "128")
                };

                if (string.IsNullOrWhiteSpace(predefined.SenderCompID) &&
                    string.IsNullOrWhiteSpace(predefined.TargetCompID) &&
                    string.IsNullOrWhiteSpace(predefined.TargetSubID) &&
                    string.IsNullOrWhiteSpace(predefined.OnBehalfOfCompID) &&
                    string.IsNullOrWhiteSpace(predefined.DeliverToCompID))
                {
                    return null;
                }

                return predefined;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load FIX defaults from WPF appsettings.");
                return null;
            }
        }

        private static string GetFixSetting(JsonElement fixElement, string key)
        {
            if (fixElement.TryGetProperty(key, out var valueElement))
            {
                var value = valueElement.ValueKind switch
                {
                    JsonValueKind.String => valueElement.GetString(),
                    JsonValueKind.Number => valueElement.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };

                return value?.Trim() ?? string.Empty;
            }

            return string.Empty;
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
            if (File.Exists(wpfAppSettings))
            {
                return wpfAppSettings;
            }

            return null;
        }

        private void EditMapping()
        {
            if (SelectedMapping == null)
            {
                StatusMessage = "Please select a mapping to edit";
                return;
            }

            var targetClientId = SelectedMapping;
            var currentClientId = _currentMapping?.ClientId;

            if (IsEditing && !string.IsNullOrWhiteSpace(currentClientId) &&
                !string.Equals(currentClientId, targetClientId, StringComparison.OrdinalIgnoreCase))
            {
                if (_hasUnsavedChanges)
                {
                    var saveChanges = ShowConfirmationDialog("Save Changes", $"Want to save your changes for \"{currentClientId}\" map?");
                    if (saveChanges)
                    {
                        SaveMapping();
                        if (_hasUnsavedChanges)
                        {
                            // Save failed, stay on current map
                            return;
                        }
                    }
                    else
                    {
                        _suppressChangeTracking = true;
                        LoadMappingDetails(targetClientId);
                        _suppressChangeTracking = false;
                        IsEditing = true;
                        _hasUnsavedChanges = false;
                        StatusMessage = $"Editing: {targetClientId}";
                        return;
                    }
                }
            }

            SelectedMapping = targetClientId;
            if (!string.Equals(_currentMapping?.ClientId, targetClientId, StringComparison.OrdinalIgnoreCase))
            {
                _suppressChangeTracking = true;
                LoadMappingDetails(targetClientId);
                _suppressChangeTracking = false;
            }

            IsEditing = true;
            _hasUnsavedChanges = false;
            StatusMessage = $"Editing: {targetClientId}";
        }

        private void SaveMapping()
        {
            if (_currentMapping == null)
            {
                StatusMessage = "No mapping to save";
                return;
            }

            try
            {
                // Validate
                if (string.IsNullOrWhiteSpace(ClientId))
                {
                    StatusMessage = "Client ID is required";
                    return;
                }

                if (FieldMappings.Count == 0)
                {
                    StatusMessage = "At least one field mapping is required";
                    return;
                }

                if (!HasRequiredTags)
                {
                    StatusMessage = "Missing required FIX fields. Please map tags 54, 55, 79, 80, and 153 before saving.";
                    return;
                }

                // Update model
                _currentMapping.ClientId = ClientId;
                _currentMapping.SenderDomain = SenderDomain;
                _currentMapping.Predefined ??= new PredefinedFields();
                _currentMapping.Predefined.SenderCompID = SenderCompId;
                _currentMapping.Predefined.TargetCompID = string.IsNullOrWhiteSpace(TargetCompId) ? "BROKER" : TargetCompId;
                _currentMapping.Predefined.TargetSubID = string.IsNullOrWhiteSpace(TargetSubId) ? null : TargetSubId;
                _currentMapping.Predefined.OnBehalfOfCompID = string.IsNullOrWhiteSpace(OnBehalfOfCompId) ? null : OnBehalfOfCompId;
                _currentMapping.Predefined.DeliverToCompID = string.IsNullOrWhiteSpace(DeliverToCompId) ? null : DeliverToCompId;

                _currentMapping.TradeAllocations.Clear();
                foreach (var kvp in FieldMappings)
                {
                    var header = kvp.Key?.Trim() ?? string.Empty;
                    var tag = kvp.Value?.Trim() ?? string.Empty;
                    if (!string.IsNullOrEmpty(header) && !string.IsNullOrEmpty(tag))
                    {
                        _currentMapping.TradeAllocations[header] = tag;
                    }
                }

                // Save to file
                var filename = $"{ClientId}_map.json";
                var filepath = Path.Combine(_configDir, filename);

                var json = JsonSerializer.Serialize(_currentMapping, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                File.WriteAllText(filepath, json);
                _logger.LogInformation("Saved mapping: {ClientId} to {File}", ClientId, filename);

                UpdateSessionConfigFiles(_currentMapping);

                IsEditing = false;
                _hasUnsavedChanges = false;
                LoadAvailableMappings();
                SelectedMapping = ClientId;

                StatusMessage = $"Mapping saved: {ClientId}";
                _mappingRepo.NotifyMappingsChanged();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error saving mapping: {ex.Message}";
                _logger.LogError(ex, "Failed to save mapping");
            }
        }

        private void DeleteMapping()
        {
            if (SelectedMapping == null)
            {
                StatusMessage = "Please select a mapping to delete";
                return;
            }

            var clientId = SelectedMapping;
            var confirmDelete = ShowConfirmationDialog("Confirm Delete", $"Are you sure you want to delete the \"{clientId}\" map?");
            if (!confirmDelete)
            {
                StatusMessage = "Delete cancelled";
                return;
            }

            try
            {
                var filename = $"{clientId}_map.json";
                var filepath = Path.Combine(_configDir, filename);
                var cliPath = Path.Combine(_cliConfigsDir, filename);
                var deployedExists = File.Exists(cliPath);

                var deleteDeployed = deployedExists
                    ? ShowConfirmationDialog("Delete Deployed Copy", $"A copy of the \"{clientId}\" map is deployed to a background processing service. Selecting Yes will delete all copies (master and deployed).")
                    : false;

                if (deployedExists && !deleteDeployed)
                {
                    StatusMessage = "Delete cancelled";
                    return;
                }

                if (File.Exists(filepath))
                {
                    var mappingForDelete = LoadMappingForSessionSync(filepath, clientId);
                    File.Delete(filepath);
                    _logger.LogInformation("Deleted mapping: {ClientId}", clientId);

                    if (mappingForDelete != null)
                    {
                        RemoveSessionConfigFiles(mappingForDelete, clientId);
                    }
                }

                if (deleteDeployed && deployedExists)
                {
                    File.Delete(cliPath);
                    _logger.LogInformation("Deleted deployed mapping copy for: {ClientId}", clientId);
                }

                LoadAvailableMappings();
                SelectedMapping = null;
                _hasUnsavedChanges = false;
                StatusMessage = deleteDeployed
                    ? $"Mapping and deployed copy deleted: {clientId}"
                    : $"Mapping deleted: {clientId}";
                _mappingRepo.NotifyMappingsChanged();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting mapping: {ex.Message}";
                _logger.LogError(ex, "Failed to delete mapping");
            }
        }

        private void Cancel()
        {
            var result = ShowCancelConfirmation();
            if (result != MessageBoxResult.Yes) return;

            IsEditing = false;
            if (!string.IsNullOrEmpty(SelectedMapping))
            {
                LoadMappingDetails(SelectedMapping);
            }
            StatusMessage = "Cancelled";
        }

        private MessageBoxResult ShowCancelConfirmation()
        {
            MessageBoxResult result = MessageBoxResult.No;

            var grid = new Grid
            {
                Margin = new Thickness(16)
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock
            {
                Text = "Are you sure you want to cancel your changes?",
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(text, 0);
            grid.Children.Add(text);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var yesButton = new Button { Content = "Yes", MinWidth = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var noButton = new Button { Content = "No", MinWidth = 70, IsCancel = true };

            buttonPanel.Children.Add(yesButton);
            buttonPanel.Children.Add(noButton);

            Grid.SetRow(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            var dialog = new Window
            {
                Title = "Confirm Cancel",
                Owner = Application.Current?.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = grid
            };

            void OnClose(MessageBoxResult r)
            {
                result = r;
                dialog.DialogResult = true;
            }

            yesButton.Click += (_, __) => OnClose(MessageBoxResult.Yes);
            noButton.Click += (_, __) => OnClose(MessageBoxResult.No);

            dialog.ShowDialog();
            return result;
        }

        private bool ShowConfirmationDialog(string title, string message)
        {
            var result = false;

            var grid = new Grid
            {
                Margin = new Thickness(16)
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(text, 0);
            grid.Children.Add(text);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var yesButton = new Button { Content = "Yes", MinWidth = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var noButton = new Button { Content = "No", MinWidth = 70, IsCancel = true };

            buttonPanel.Children.Add(yesButton);
            buttonPanel.Children.Add(noButton);

            Grid.SetRow(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            var dialog = new Window
            {
                Title = title,
                Owner = Application.Current?.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = grid
            };

            void CloseWith(bool value)
            {
                result = value;
                dialog.DialogResult = true;
            }

            yesButton.Click += (_, __) => CloseWith(true);
            noButton.Click += (_, __) => CloseWith(false);

            dialog.ShowDialog();
            return result;
        }

        private void MarkDirty()
        {
            if (_suppressChangeTracking)
            {
                return;
            }

            _hasUnsavedChanges = true;
        }

        private void AddField()
        {
            if (string.IsNullOrWhiteSpace(NewFieldName) || string.IsNullOrWhiteSpace(NewFieldTag))
            {
                StatusMessage = "Field name and tag are required";
                return;
            }

            // Check if field already exists
            if (FieldMappings.Any(x => x.Key.Equals(NewFieldName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = "Field already exists";
                return;
            }

            FieldMappings.Add(new KeyValuePair<string, string>(NewFieldName.Trim(), NewFieldTag.Trim()));
            NewFieldName = string.Empty;
            NewFieldTag = string.Empty;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(MissingRequiredTagsMessage));
            MarkDirty();
        }

        private void UpdateField()
        {
            if (!SelectedFieldMapping.HasValue)
            {
                StatusMessage = "Select a field to update";
                return;
            }

            if (string.IsNullOrWhiteSpace(NewFieldName) || string.IsNullOrWhiteSpace(NewFieldTag))
            {
                StatusMessage = "Field name and tag are required";
                return;
            }

            var duplicate = FieldMappings.Any(x =>
                !string.Equals(x.Key, SelectedFieldMapping.Value.Key, StringComparison.OrdinalIgnoreCase) &&
                x.Key.Equals(NewFieldName, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                StatusMessage = "Field name already exists";
                return;
            }

            var index = FieldMappings.IndexOf(SelectedFieldMapping.Value);
            if (index >= 0)
            {
                var updated = new KeyValuePair<string, string>(NewFieldName.Trim(), NewFieldTag.Trim());
                FieldMappings[index] = updated;
                SelectedFieldMapping = updated;
                StatusMessage = "✅ Field updated";
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(MissingRequiredTagsMessage));
                MarkDirty();
            }
        }

        private void RemoveField()
        {
            if (SelectedFieldMapping == null) return;

            FieldMappings.Remove(SelectedFieldMapping.Value);
            SelectedFieldMapping = null;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(MissingRequiredTagsMessage));
            MarkDirty();
        }

        private void TestMapping()
        {
            StatusMessage = "Testing mapping... (Create a sample spreadsheet and try direct ingestion)";
            _logger.LogInformation("To test this mapping, use the Direct Ingestion tab with a sample spreadsheet");
        }

        private void DeployMapping()
        {
            if (SelectedMapping == null) return;

            try
            {
                var filename = $"{SelectedMapping}_map.json";
                var sourcePath = Path.Combine(_configDir, filename);

                if (!File.Exists(sourcePath))
                {
                    StatusMessage = "Mapping file not found";
                    return;
                }

                // Verify file integrity
                var testMapping = MappingConfig.Load(sourcePath);
                if (testMapping == null)
                {
                    StatusMessage = "Invalid mapping configuration";
                    return;
                }

                // Destination: staging/incoming folder inside the CLI configs folder
                var incomingDir = Path.Combine(_cliConfigsDir, "incoming");
                Directory.CreateDirectory(incomingDir);

                var incomingPath = Path.Combine(incomingDir, filename);
                var tempFile = Path.Combine(incomingDir, $"{filename}.{Guid.NewGuid():N}.tmp");

                // Copy via a temp file in the incoming folder to avoid writing directly into the CLI's active file.
                using (var srcStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destStream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    srcStream.CopyTo(destStream);
                }

                // Try to atomically move/replace the temp file into the incoming filename.
                var deployed = false;
                const int maxAttempts = 6;
                const int delayMs = 200;
                for (int attempt = 1; attempt <= maxAttempts && !deployed; attempt++)
                {
                    try
                    {
                        if (File.Exists(incomingPath))
                        {
                            File.Replace(tempFile, incomingPath, null);
                        }
                        else
                        {
                            File.Move(tempFile, incomingPath);
                        }

                        deployed = true;
                    }
                    catch (IOException ioEx) when (attempt < maxAttempts)
                    {
                        _logger.LogWarning(ioEx, "Attempt {Attempt} to stage mapping {ClientId} to incoming folder failed; retrying...", attempt, SelectedMapping);
                        Thread.Sleep(delayMs);
                    }
                    catch (UnauthorizedAccessException uaEx) when (attempt < maxAttempts)
                    {
                        _logger.LogWarning(uaEx, "Attempt {Attempt} to stage mapping {ClientId} to incoming folder failed (access); retrying...", attempt, SelectedMapping);
                        Thread.Sleep(delayMs);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to stage mapping {ClientId}", SelectedMapping);
                        break;
                    }
                }

                if (!deployed)
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* best-effort cleanup */ }
                    StatusMessage = $"Failed to stage mapping: incoming file is in use. Close any process using '{incomingDir}' and try again.";
                    _logger.LogError("Failed to stage mapping {ClientId} into incoming folder {IncomingDir}", SelectedMapping, incomingDir);
                    return;
                }

                // Optionally update CLI cfg session entries (keeps CLI session config attempts in sync)
                UpdateCliSessionConfigFile(testMapping);

                // Expose incoming path to UI for user verification
                IncomingFolderPath = incomingPath;
                StatusMessage = $"Mapping '{SelectedMapping}' staged to incoming folder: {incomingPath}";
                _logger.LogInformation("Staged mapping: {ClientId} to {Path}", SelectedMapping, incomingPath);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to deploy mapping: {ex.Message}";
                _logger.LogError(ex, "Failed to deploy mapping");
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void UpdateSessionConfigFiles(MappingConfig mapping)
        {
            var senderCompId = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? string.Empty;
            var targetCompId = mapping.Predefined?.TargetCompID ?? "EXECUTOR";

            if (string.IsNullOrWhiteSpace(senderCompId) || string.IsNullOrWhiteSpace(targetCompId))
            {
                return;
            }

            var wpfFixCfgPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.cfg");
            var executorCfgPath = ResolveExecutorCfgPath();

            TrySyncSessionSection(
                wpfFixCfgPath,
                senderCompId,
                BuildWpfSessionLines(senderCompId, targetCompId),
                remove: false);

            if (!string.IsNullOrWhiteSpace(executorCfgPath))
            {
                TrySyncSessionSection(
                    executorCfgPath,
                    targetCompId,
                    BuildExecutorSessionLines(targetCompId, senderCompId),
                    remove: false);
            }
        }

        private void UpdateCliSessionConfigFile(MappingConfig mapping)
        {
            var senderCompId = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? string.Empty;
            var targetCompId = mapping.Predefined?.TargetCompID ?? "EXECUTOR";

            if (string.IsNullOrWhiteSpace(senderCompId) || string.IsNullOrWhiteSpace(targetCompId))
            {
                return;
            }

            var cliFixCfgPath = ResolveCliFixCfgPath();
            if (string.IsNullOrWhiteSpace(cliFixCfgPath))
            {
                _logger.LogWarning("CLI FIX42.cfg not found for session sync.");
                return;
            }

            TrySyncSessionSection(
                cliFixCfgPath,
                senderCompId,
                BuildWpfSessionLines(senderCompId, targetCompId),
                remove: false);
        }

        private void RemoveSessionConfigFiles(MappingConfig mapping, string clientId)
        {
            var senderCompId = mapping.Predefined?.SenderCompID
                ?? mapping.ClientId
                ?? clientId
                ?? string.Empty;
            var targetCompId = mapping.Predefined?.TargetCompID ?? "EXECUTOR";

            if (string.IsNullOrWhiteSpace(senderCompId))
            {
                return;
            }

            var wpfFixCfgPath = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.cfg");
            var executorCfgPath = ResolveExecutorCfgPath();

            TrySyncSessionSection(
                wpfFixCfgPath,
                senderCompId,
                BuildWpfSessionLines(senderCompId, targetCompId),
                remove: true);

            if (!string.IsNullOrWhiteSpace(executorCfgPath))
            {
                TrySyncSessionSection(
                    executorCfgPath,
                    targetCompId,
                    BuildExecutorSessionLines(targetCompId, senderCompId),
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
                if (_currentMapping != null &&
                    string.Equals(_currentMapping.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                {
                    return _currentMapping;
                }
            }

            return null;
        }

        private bool TrySyncSessionSection(
            string configPath,
            string senderCompId,
            List<string> sessionLines,
            bool remove)
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                _logger.LogWarning("FIX session config not found at {Path}", configPath);
                return false;
            }

            try
            {
                var updated = SyncSessionSection(configPath, senderCompId, sessionLines, remove, out var updatedLines);
                if (updated)
                {
                    File.WriteAllLines(configPath, updatedLines);
                    _logger.LogInformation("Updated FIX session config at {Path}", configPath);
                }
                else
                {
                    _logger.LogInformation("FIX session config already up-to-date at {Path}", configPath);
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
            List<string> sessionLines,
            bool remove,
            out List<string> updatedLines)
        {
            updatedLines = new List<string>();

            var lines = File.ReadAllLines(configPath);
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
                var existingBeginString = GetConfigValue(section.Lines, "BeginString");

                if (!string.Equals(existingSender, senderCompId, StringComparison.OrdinalIgnoreCase) ||
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
                if (string.IsNullOrWhiteSpace(line)) continue;
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var k = line.Substring(0, idx).Trim();
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                return line.Substring(idx + 1).Trim();
            }

            return null;
        }

        private static List<string> BuildWpfSessionLines(string senderCompId, string targetCompId)
        {
            return new List<string>
            {
                "BeginString=FIX.4.2",
                "FileStorePath=store",
                $"SenderCompID={senderCompId}",
                $"TargetCompID={targetCompId}"
            };
        }

        private static List<string> BuildExecutorSessionLines(string senderCompId, string targetCompId)
        {
            return new List<string>
            {
                "BeginString=FIX.4.2",
                $"SenderCompID={senderCompId}",
                $"TargetCompID={targetCompId}",
                "FileStorePath=store",
                "DataDictionary=../../spec/fix/FIX42.xml"
            };
        }

        private string? ResolveCliFixCfgPath()
        {
            if (!string.IsNullOrWhiteSpace(_cliConfigsDir) && Directory.Exists(_cliConfigsDir))
            {
                var cliBaseDir = Directory.GetParent(_cliConfigsDir)?.FullName;
                if (!string.IsNullOrWhiteSpace(cliBaseDir))
                {
                    var cfgPath = Path.Combine(cliBaseDir, "cfg", "FIX42.cfg");
                    if (File.Exists(cfgPath))
                    {
                        return cfgPath;
                    }
                }
            }

            var baseDir = AppContext.BaseDirectory;
            var solutionRoot = FindAncestorWithFile(baseDir, "*.sln", maxLevels: 8);
            if (string.IsNullOrWhiteSpace(solutionRoot))
            {
                return null;
            }

            var cliProj = Path.Combine(solutionRoot, "src", "FixFlow.TradeAllocBridge.CLI");
            var binDir = Path.Combine(cliProj, "bin");
            if (!Directory.Exists(binDir))
            {
                return null;
            }

            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var cfgDir = Path.Combine(binDir, configuration);
                if (!Directory.Exists(cfgDir)) continue;

                foreach (var tfmDir in Directory.GetDirectories(cfgDir))
                {
                    var cfgPath = Path.Combine(tfmDir, "cfg", "FIX42.cfg");
                    if (File.Exists(cfgPath))
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
            return File.Exists(executorCfg) ? executorCfg : null;
        }

        private static bool TryDeployMappingCopy(string sourcePath, string destinationPath, out string? error)
        {
            error = null;
            var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

            try
            {
                File.Copy(sourcePath, tempPath, overwrite: true);

                const int maxAttempts = 5;
                const int retryDelayMs = 200;

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        if (File.Exists(destinationPath))
                        {
                            File.Replace(tempPath, destinationPath, null);
                        }
                        else
                        {
                            File.Move(tempPath, destinationPath);
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
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }

        // Helper(s) to robustly resolve CLI configs directory
        private static string ResolveCliConfigsDir()
        {
            const string envVar = "FIXFLOW_CLI_CONFIGS";

            // 0) environment override
            try
            {
                var env = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                    return Path.GetFullPath(env);
            }
            catch { /* ignore and continue */ }

            var baseDir = AppContext.BaseDirectory;

            // 1) Prefer an explicit CLI project folder under the solution (keeps WPF from picking its own configs)
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
                                if (!Directory.Exists(cfgDir)) continue;

                                foreach (var tfmDir in Directory.GetDirectories(cfgDir))
                                {
                                    var configsDir = Path.Combine(tfmDir, "configs");
                                    if (Directory.Exists(configsDir))
                                        return Path.GetFullPath(configsDir);
                                }
                            }
                        }
                    }
                }
            }
            catch { /* continue to other strategies */ }

            // 2) Local/nearby 'configs' (legacy heuristics)
            try
            {
                var candidate = Path.Combine(baseDir, "configs");
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);

                candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "configs"));
                if (Directory.Exists(candidate)) return candidate;
            }
            catch { /* continue */ }

            // 3) Search src/*CLI* as fallback (if solution layout differs)
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
                            if (!Directory.Exists(binDir)) continue;

                            foreach (var configuration in new[] { "Debug", "Release" })
                            {
                                var cfgDir = Path.Combine(binDir, configuration);
                                if (!Directory.Exists(cfgDir)) continue;

                                foreach (var tfmDir in Directory.GetDirectories(cfgDir))
                                {
                                    var configsDir = Path.Combine(tfmDir, "configs");
                                    if (Directory.Exists(configsDir))
                                        return Path.GetFullPath(configsDir);
                                }
                            }
                        }
                    }
                }
            }
            catch { /* ignore */ }

            // 4) Upward search for any 'configs' folder
            try
            {
                var di = new DirectoryInfo(baseDir);
                for (int depth = 0; depth < 8 && di != null; depth++, di = di.Parent)
                {
                    var found = di.GetDirectories("configs", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (found != null) return found.FullName;
                }
            }
            catch { }

            // 5) Fallback to per-user AppData
            try
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FixFlow", "configs");
                Directory.CreateDirectory(appData);
                return appData;
            }
            catch
            {
                // last resort
                return baseDir;
            }
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
    }

    /// <summary>Result of a FIX message generation during testing</summary>
    public class FixMessageResult
    {
        public string AllocId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public int TradeCount { get; set; }
        public string RawFix { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}

