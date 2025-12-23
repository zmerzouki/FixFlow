using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using RJF.TradeAllocBridge.Core.Config;
using RJF.TradeAllocBridge.Core.Mapping;
using System.ComponentModel;

namespace RJF.TradeAllocBridge.WPF.ViewModels
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
        private bool _isEditing;
        private string _statusMessage = "Ready";
        private int _testTradesCount;
        private ObservableCollection<FixMessageResult> _testResults;

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
        public ICommand RemoveFieldCommand { get; }
        public ICommand TestMappingCommand { get; }
        public ICommand DeployMappingCommand { get; }

        public MapEditorViewModel(FixMappingRepository mappingRepo, ILogger<MapEditorViewModel> logger)
        {
            _mappingRepo = mappingRepo;
            _logger = logger;
            _configDir = _mappingRepo.BaseDirectory;
            _cliConfigsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RJF.TradeAllocBridge.CLI", "bin", "Debug", "net8.0-windows", "configs"));
            _testResults = new ObservableCollection<FixMessageResult>();

            AvailableMappings = new ObservableCollection<string>();
            FieldMappings = new ObservableCollection<KeyValuePair<string, string>>();

            NewMappingCommand = new RelayCommand(NewMapping);
            EditMappingCommand = new RelayCommand(EditMapping, () => SelectedMapping != null);
            SaveMappingCommand = new RelayCommand(SaveMapping, () => CanSave);
            DeleteMappingCommand = new RelayCommand(DeleteMapping, () => SelectedMapping != null && !IsEditing);
            CancelCommand = new RelayCommand(Cancel, () => IsEditing);
            AddFieldCommand = new RelayCommand(AddField, () => CanAddField);
            RemoveFieldCommand = new RelayCommand(RemoveField, () => SelectedFieldMapping != null);
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
                StatusMessage = $"✅ Loaded {AvailableMappings.Count} mapping(s)";
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Error loading mappings: {ex.Message}";
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
            try
            {
                var mapFile = Path.Combine(_configDir, $"{clientId}_map.json");
                if (File.Exists(mapFile))
                {
                    _currentMapping = MappingConfig.Load(mapFile);
                    SenderCompId = _currentMapping.Predefined?.SenderCompID ?? string.Empty;
                    TargetCompId = _currentMapping.Predefined?.TargetCompID ?? string.Empty;

                    FieldMappings.Clear();
                    foreach (var kvp in _currentMapping.TradeAllocations.OrderBy(x => x.Key))
                    {
                        FieldMappings.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Value));
                    }

                    StatusMessage = $"✅ Loaded mapping: {clientId}";
                    OnPropertyChanged(nameof(ClientId));
                    OnPropertyChanged(nameof(SenderDomain));
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Error loading mapping details: {ex.Message}";
                _logger.LogError(ex, "Failed to load mapping details for {ClientId}", clientId);
            }
        }

        public string ClientId
        {
            get => _currentMapping?.ClientId ?? string.Empty;
            set
            {
                if (_currentMapping != null && _currentMapping.ClientId != value)
                {
                    _currentMapping.ClientId = value;
                    OnPropertyChanged(nameof(ClientId));
                    if (IsEditing && (string.IsNullOrWhiteSpace(SenderCompId) || SenderCompId == "NEW_CLIENT"))
                    {
                        SenderCompId = value;
                    }
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
                if (_currentMapping != null && _currentMapping.SenderDomain != value)
                {
                    _currentMapping.SenderDomain = value;
                    OnPropertyChanged(nameof(SenderDomain));
                }
            }
        }

        public string SenderCompId
        {
            get => _senderCompId;
            set
            {
                if (_senderCompId != value)
                {
                    _senderCompId = value;
                    OnPropertyChanged(nameof(SenderCompId));
                }
            }
        }

        public string TargetCompId
        {
            get => _targetCompId;
            set
            {
                if (_targetCompId != value)
                {
                    _targetCompId = value;
                    OnPropertyChanged(nameof(TargetCompId));
                }
            }
        }

        public string TargetSubId
        {
            get => _targetSubId;
            set
            {
                if (_targetSubId != value)
                {
                    _targetSubId = value;
                    OnPropertyChanged(nameof(TargetSubId));
                }
            }
        }

        public string OnBehalfOfCompId
        {
            get => _onBehalfOfCompId;
            set
            {
                if (_onBehalfOfCompId != value)
                {
                    _onBehalfOfCompId = value;
                    OnPropertyChanged(nameof(OnBehalfOfCompId));
                }
            }
        }

        public string DeliverToCompId
        {
            get => _deliverToCompId;
            set
            {
                if (_deliverToCompId != value)
                {
                    _deliverToCompId = value;
                    OnPropertyChanged(nameof(DeliverToCompId));
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
                    OnPropertyChanged(nameof(CanAddField));
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
                    OnPropertyChanged(nameof(CanAddField));
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
                OnPropertyChanged(nameof(SelectedFieldMapping));
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

        public bool CanAddField => !string.IsNullOrWhiteSpace(NewFieldName) && !string.IsNullOrWhiteSpace(NewFieldTag);

        public bool CanSave => IsEditing && !string.IsNullOrWhiteSpace(ClientId) && FieldMappings.Count > 0;

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
            _currentMapping = new MappingConfig
            {
                ClientId = "NEW_CLIENT",
                SenderDomain = "example.com",
                Predefined = new PredefinedFields
                {
                    SenderCompID = "NEW_CLIENT",
                    TargetCompID = "RJSYNUAT"
                },
                TradeAllocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            ClientId = "NEW_CLIENT";
            SenderDomain = "example.com";
            SenderCompId = "NEW_CLIENT";
            TargetCompId = "RJSYNUAT";
            TargetSubId = string.Empty;
            OnBehalfOfCompId = string.Empty;
            DeliverToCompId = string.Empty;
            FieldMappings.Clear();
            NewFieldName = string.Empty;
            NewFieldTag = string.Empty;

            IsEditing = true;
            StatusMessage = "📝 Creating new mapping...";
        }

        private void EditMapping()
        {
            if (SelectedMapping == null)
            {
                StatusMessage = "⚠️ Please select a mapping to edit";
                return;
            }

            IsEditing = true;
            StatusMessage = $"✏️ Editing: {SelectedMapping}";
        }

        private void SaveMapping()
        {
            if (_currentMapping == null)
            {
                StatusMessage = "❌ No mapping to save";
                return;
            }

            try
            {
                // Validate
                if (string.IsNullOrWhiteSpace(ClientId))
                {
                    StatusMessage = "❌ Client ID is required";
                    return;
                }

                if (FieldMappings.Count == 0)
                {
                    StatusMessage = "❌ At least one field mapping is required";
                    return;
                }

                // Update model
                _currentMapping.ClientId = ClientId;
                _currentMapping.SenderDomain = SenderDomain;
                _currentMapping.Predefined ??= new PredefinedFields();
                _currentMapping.Predefined.SenderCompID = SenderCompId;
                _currentMapping.Predefined.TargetCompID = string.IsNullOrWhiteSpace(TargetCompId) ? "RJSYNUAT" : TargetCompId;

                _currentMapping.TradeAllocations.Clear();
                foreach (var kvp in FieldMappings)
                {
                    _currentMapping.TradeAllocations[kvp.Key] = kvp.Value;
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
                _logger.LogInformation("✅ Saved mapping: {ClientId} to {File}", ClientId, filename);

                IsEditing = false;
                    LoadAvailableMappings();
                SelectedMapping = ClientId;

                StatusMessage = $"✅ Mapping saved: {ClientId}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Error saving mapping: {ex.Message}";
                _logger.LogError(ex, "Failed to save mapping");
            }
        }

        private void DeleteMapping()
        {
            if (SelectedMapping == null)
            {
                StatusMessage = "⚠️ Please select a mapping to delete";
                return;
            }

            try
            {
                var filename = $"{SelectedMapping}_map.json";
                var filepath = Path.Combine(_configDir, filename);
                var cliPath = Path.Combine(_cliConfigsDir, filename);

                if (File.Exists(filepath))
                {
                    File.Delete(filepath);
                    _logger.LogInformation("🗑️ Deleted mapping: {ClientId}", SelectedMapping);
                    LoadAvailableMappings();
                    StatusMessage = $"✅ Mapping deleted: {SelectedMapping}";
                }

                if (File.Exists(cliPath))
                {
                    File.Delete(cliPath);
                    _logger.LogInformation("🗑️ Deleted deployed mapping copy for: {ClientId}", SelectedMapping);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Error deleting mapping: {ex.Message}";
                _logger.LogError(ex, "Failed to delete mapping");
            }
        }

        private void Cancel()
        {
            IsEditing = false;
            if (!string.IsNullOrEmpty(SelectedMapping))
            {
                LoadMappingDetails(SelectedMapping);
            }
            StatusMessage = "Cancelled";
        }

        private void AddField()
        {
            if (string.IsNullOrWhiteSpace(NewFieldName) || string.IsNullOrWhiteSpace(NewFieldTag))
            {
                StatusMessage = "⚠️ Field name and tag are required";
                return;
            }

            // Check if field already exists
            if (FieldMappings.Any(x => x.Key.Equals(NewFieldName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = "⚠️ Field already exists";
                return;
            }

            FieldMappings.Add(new KeyValuePair<string, string>(NewFieldName, NewFieldTag));
            NewFieldName = string.Empty;
            NewFieldTag = string.Empty;
            OnPropertyChanged(nameof(CanSave));
        }

        private void RemoveField()
        {
            if (SelectedFieldMapping == null) return;

            FieldMappings.Remove(SelectedFieldMapping.Value);
            SelectedFieldMapping = null;
            OnPropertyChanged(nameof(CanSave));
        }

        private void TestMapping()
        {
            StatusMessage = "🧪 Testing mapping... (Create a sample spreadsheet and try direct ingestion)";
            _logger.LogInformation("ℹ️ To test this mapping, use the Direct Ingestion tab with a sample spreadsheet");
        }

        private void DeployMapping()
        {
            if (SelectedMapping == null) return;

            try
            {
                var filename = $"{SelectedMapping}_map.json";
                var sourcePath = Path.Combine(_configDir, filename);
                var cliPath = Path.Combine(_cliConfigsDir, filename);

                if (!File.Exists(sourcePath))
                {
                    StatusMessage = "❌ Mapping file not found";
                    return;
                }

                // Verify file integrity
                var testMapping = MappingConfig.Load(sourcePath);
                if (testMapping == null)
                {
                    StatusMessage = "❌ Invalid mapping configuration";
                    return;
                }

                Directory.CreateDirectory(_cliConfigsDir);
                File.Copy(sourcePath, cliPath, overwrite: true);

                StatusMessage = $"✅ Mapping '{SelectedMapping}' deployed to email attachment processing";
                _logger.LogInformation("🚀 Deployed mapping: {ClientId} to {Path}", SelectedMapping, cliPath);
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ Error deploying mapping: {ex.Message}";
                _logger.LogError(ex, "Failed to deploy mapping");
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>Result of a FIX message generation during testing</summary>
    public class FixMessageResult
    {
        public string AllocId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public int TradeCount { get; set; }
        public string RawFix { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
