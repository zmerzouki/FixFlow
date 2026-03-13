using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using QuickFix;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;
using FixFlow.TradeAllocBridge.Core.Mapping;
using FixFlow.TradeAllocBridge.Core.Reporting;

namespace FixFlow.TradeAllocBridge.WPF.ViewModels
{
    public class AllocationProcessorViewModel : INotifyPropertyChanged
    {
        private readonly ExcelParser _excelParser;
        private readonly FixApp _fixApp;
        private readonly FixEngine _fixEngine;
        private readonly FixClient _fixClient;
        private readonly ValidationReport _validationReport;
        private readonly FixMappingRepository _mappingRepo;
        private readonly AppConfig _appConfig;
        private readonly ILogger<AllocationProcessorViewModel> _logger;
        private readonly ILoggerFactory _loggerFactory;

        private KeyValuePair<string, string>? _selectedClient;
        private MappingConfig? _selectedMapping;
        private string? _selectedFilePath;
        private long _selectedFileSizeKb;
        private string _enteredOnBehalfOfCompId = string.Empty;
        private bool _isDryRun = true;
        private bool _isProcessing;
        private double _processProgress;
        private string _statusMessage = "Ready";
        private bool _showResults;
        private int _processedTradesCount;
        private int _sentAllocationsCount;
        private int _allocationsSubmittedCount;
        private string _resultMessage = "";
        private string _resultSuccessMessage = "";
        private string _resultFailureMessage = "";
        private int _successfulAllocationsCount;
        private int _failedAllocationsCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<KeyValuePair<string, string>> AvailableClients { get; }
        public ObservableCollection<FixMessageResult> GeneratedMessages { get; }
        public ICommand BrowseFileCommand { get; }
        public ICommand ProcessCommand { get; }
        public ICommand DiscardCommand { get; }
        public ICommand ViewLogsCommand { get; }

        public AllocationProcessorViewModel(
            ExcelParser excelParser,
            FixApp fixApp,
            FixEngine fixEngine,
            FixClient fixClient,
            ValidationReport validationReport,
            FixMappingRepository mappingRepo,
            AppConfig appConfig,
            ILoggerFactory loggerFactory,
            ILogger<AllocationProcessorViewModel> logger)
        {
            _excelParser = excelParser;
            _fixApp = fixApp;
            _fixEngine = fixEngine;
            _fixClient = fixClient;
            _validationReport = validationReport;
            _mappingRepo = mappingRepo;
            _appConfig = appConfig;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _logger.LogInformation("Mapping repository base directory: {BaseDirectory}", _mappingRepo.BaseDirectory);

            AvailableClients = new ObservableCollection<KeyValuePair<string, string>>();
            GeneratedMessages = new ObservableCollection<FixMessageResult>();
            LoadAvailableClients();

            _mappingRepo.MappingsChanged += OnMappingsChanged;

            BrowseFileCommand = new RelayCommand(BrowseFile);
            ProcessCommand = new RelayCommand(Process, CanProcessFile);
            DiscardCommand = new RelayCommand(Discard, CanDiscardForm);
            ViewLogsCommand = new RelayCommand(ViewLogs);
        }

        private void LoadAvailableClients(bool refreshSelection = false)
        {
            var previousSelection = refreshSelection ? SelectedClient?.Key : null;

            AvailableClients.Clear();

            try
            {
                var mappings = _mappingRepo.GetAll();
                foreach (var mapping in mappings.OrderBy(m => m.ClientId))
                {
                    var senderDomain = mapping.SenderDomain?.Trim();
                    var display = string.IsNullOrWhiteSpace(senderDomain)
                        ? mapping.ClientId
                        : $"{mapping.ClientId} ({senderDomain})";
                    AvailableClients.Add(new KeyValuePair<string, string>(
                        mapping.ClientId,
                        display
                    ));
                }

                _logger.LogInformation("Loaded {Count} available clients", AvailableClients.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load available clients");
                StatusMessage = $"Error loading clients: {ex.Message}";
            }

            if (refreshSelection)
            {
                SelectedClient = null;

                if (!string.IsNullOrWhiteSpace(previousSelection))
                {
                    var restoredSelection = AvailableClients.FirstOrDefault(c => c.Key == previousSelection);
                    if (!string.IsNullOrWhiteSpace(restoredSelection.Key))
                    {
                        SelectedClient = restoredSelection;
                    }
                }
            }
        }

        private void OnMappingsChanged(object? sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher?.CheckAccess() == false)
            {
                dispatcher.Invoke(() => LoadAvailableClients(refreshSelection: true));
            }
            else
            {
                LoadAvailableClients(refreshSelection: true);
            }
        }

        public KeyValuePair<string, string>? SelectedClient
        {
            get => _selectedClient;
            set
            {
                if (!Nullable.Equals(_selectedClient, value))
                {
                    _selectedClient = value;
                    _selectedMapping = null;
                    if (!IsDefaultClientSelected)
                    {
                        EnteredOnBehalfOfCompId = string.Empty;
                    }

                    if (value.HasValue)
                    {
                        try
                        {
                            var mapPath = Path.Combine(_mappingRepo.BaseDirectory, $"{value.Value.Key}_map.json");
                            if (File.Exists(mapPath))
                            {
                                _selectedMapping = MappingConfig.Load(mapPath);
                            }
                            else
                            {
                                StatusMessage = $"Mapping file not found for client {value.Value.Key}";
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to load mapping for {Client}", value.Value.Key);
                            StatusMessage = $"Error loading mapping: {ex.Message}";
                        }
                    }

                    OnPropertyChanged(nameof(SelectedClient));
                    OnPropertyChanged(nameof(SelectedClientId));
                    OnPropertyChanged(nameof(SelectedClientDomain));
                    OnPropertyChanged(nameof(SelectedClientFix));
                    OnPropertyChanged(nameof(MappedFieldsCount));
                    OnPropertyChanged(nameof(IsDefaultClientSelected));
                    OnPropertyChanged(nameof(RequiresOnBehalfOfCompId));
                    NotifyCanProcessChanged();
                    NotifyDiscardChanged();
                }
            }
        }

        public string? SelectedClientId
        {
            get
            {
                return _selectedMapping?.ClientId;
            }
        }

        public string? SelectedClientDomain
        {
            get
            {
                return _selectedMapping?.SenderDomain;
            }
        }

        public string? SelectedClientFix
        {
            get
            {
                if (_selectedMapping?.Predefined is null) return null;
                return $"{_selectedMapping.Predefined.SenderCompID} -> {_selectedMapping.Predefined.TargetCompID}";
            }
        }

        public bool IsDefaultClientSelected =>
            SelectedClient.HasValue &&
            string.Equals(SelectedClient.Value.Key, "DEFAULT", StringComparison.OrdinalIgnoreCase);

        public bool RequiresOnBehalfOfCompId => IsDefaultClientSelected;

        public string EnteredOnBehalfOfCompId
        {
            get => _enteredOnBehalfOfCompId;
            set
            {
                var normalized = value?.ToUpperInvariant() ?? string.Empty;
                if (_enteredOnBehalfOfCompId != normalized)
                {
                    _enteredOnBehalfOfCompId = normalized;
                    OnPropertyChanged(nameof(EnteredOnBehalfOfCompId));
                    NotifyCanProcessChanged();
                    NotifyDiscardChanged();
                }
            }
        }

        public int MappedFieldsCount
        {
            get
            {
                return _selectedMapping?.TradeAllocations.Count ?? 0;
            }
        }

        public string? SelectedFilePath
        {
            get => _selectedFilePath;
            set
            {
                if (_selectedFilePath != value)
                {
                    _selectedFilePath = value;
                    OnPropertyChanged(nameof(SelectedFilePath));
                    OnPropertyChanged(nameof(SelectedFileName));
                    UpdateSelectedFileSize();
                    NotifyCanProcessChanged();
                    NotifyDiscardChanged();
                }
            }
        }

        public string? SelectedFileName => Path.GetFileName(SelectedFilePath);

        public long SelectedFileSizeKb
        {
            get => _selectedFileSizeKb;
            private set
            {
                if (_selectedFileSizeKb != value)
                {
                    _selectedFileSizeKb = value;
                    OnPropertyChanged(nameof(SelectedFileSizeKb));
                }
            }
        }

        private void UpdateSelectedFileSize()
        {
            if (string.IsNullOrWhiteSpace(_selectedFilePath) || !File.Exists(_selectedFilePath))
            {
                SelectedFileSizeKb = 0;
                return;
            }

            var length = new FileInfo(_selectedFilePath).Length;
            SelectedFileSizeKb = (long)Math.Ceiling(length / 1024.0);
        }

        public bool IsDryRun
        {
            get => _isDryRun;
            set
            {
                if (_isDryRun != value)
                {
                    _isDryRun = value;
                    OnPropertyChanged(nameof(IsDryRun));
                    NotifyDiscardChanged();
                }
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    OnPropertyChanged(nameof(IsProcessing));
                    NotifyCanProcessChanged();
                }
            }
        }

        public double ProcessProgress
        {
            get => _processProgress;
            set
            {
                if (_processProgress != value)
                {
                    _processProgress = value;
                    OnPropertyChanged(nameof(ProcessProgress));
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

        public bool ShowResults
        {
            get => _showResults;
            set
            {
                if (_showResults != value)
                {
                    _showResults = value;
                    OnPropertyChanged(nameof(ShowResults));
                    NotifyDiscardChanged();
                }
            }
        }

        public int ProcessedTradesCount
        {
            get => _processedTradesCount;
            set
            {
                if (_processedTradesCount != value)
                {
                    _processedTradesCount = value;
                    OnPropertyChanged(nameof(ProcessedTradesCount));
                    OnPropertyChanged(nameof(SuccessRate));
                }
            }
        }

        public int SentAllocationsCount
        {
            get => _sentAllocationsCount;
            set
            {
                if (_sentAllocationsCount != value)
                {
                    _sentAllocationsCount = value;
                    OnPropertyChanged(nameof(SentAllocationsCount));
                    OnPropertyChanged(nameof(SuccessRate));
                }
            }
        }

        public int AllocationsSubmittedCount
        {
            get => _allocationsSubmittedCount;
            set
            {
                if (_allocationsSubmittedCount != value)
                {
                    _allocationsSubmittedCount = value;
                    OnPropertyChanged(nameof(AllocationsSubmittedCount));
                    OnPropertyChanged(nameof(SuccessAllocationPercentage));
                    OnPropertyChanged(nameof(FailedAllocationPercentage));
                }
            }
        }

        public string SuccessRate => ProcessedTradesCount > 0
            ? $"{(SentAllocationsCount * 100.0 / ProcessedTradesCount):F1}%"
            : "N/A";

        public int SuccessfulAllocationsCount
        {
            get => _successfulAllocationsCount;
            set
            {
                if (_successfulAllocationsCount != value)
                {
                    _successfulAllocationsCount = value;
                    OnPropertyChanged(nameof(SuccessfulAllocationsCount));
                    OnPropertyChanged(nameof(SuccessAllocationPercentage));
                    OnPropertyChanged(nameof(FailedAllocationPercentage));
                }
            }
        }

        public int FailedAllocationsCount
        {
            get => _failedAllocationsCount;
            set
            {
                if (_failedAllocationsCount != value)
                {
                    _failedAllocationsCount = value;
                    OnPropertyChanged(nameof(FailedAllocationsCount));
                    OnPropertyChanged(nameof(FailedAllocationPercentage));
                    OnPropertyChanged(nameof(SuccessAllocationPercentage));
                }
            }
        }

        public double SuccessAllocationPercentage => AllocationsSubmittedCount > 0
            ? SuccessfulAllocationsCount * 100.0 / AllocationsSubmittedCount
            : 0;

        public double FailedAllocationPercentage => AllocationsSubmittedCount > 0
            ? FailedAllocationsCount * 100.0 / AllocationsSubmittedCount
            : 0;

        public string ResultMessage
        {
            get => _resultMessage;
            set
            {
                if (_resultMessage != value)
                {
                    _resultMessage = value;
                    OnPropertyChanged(nameof(ResultMessage));
                }
            }
        }

        public string ResultSuccessMessage
        {
            get => _resultSuccessMessage;
            set
            {
                if (_resultSuccessMessage != value)
                {
                    _resultSuccessMessage = value;
                    OnPropertyChanged(nameof(ResultSuccessMessage));
                }
            }
        }

        public string ResultFailureMessage
        {
            get => _resultFailureMessage;
            set
            {
                if (_resultFailureMessage != value)
                {
                    _resultFailureMessage = value;
                    OnPropertyChanged(nameof(ResultFailureMessage));
                }
            }
        }

        public bool CanProcess => CanProcessFile();

        public void SelectFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                SelectedFilePath = filePath;
            }
        }

        private void BrowseFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Excel files (*.xlsx;*.xls)|*.xlsx;*.xls|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                Title = "Select Allocation Spreadsheet"
            };

            if (dialog.ShowDialog() == true)
            {
                SelectedFilePath = dialog.FileName;
            }
        }

        private async void Process()
        {
            var selectedClient = SelectedClient;
            if (!selectedClient.HasValue || string.IsNullOrEmpty(SelectedFilePath))
            {
                StatusMessage = "Please select a client and a file.";
                return;
            }

            if (RequiresOnBehalfOfCompId && string.IsNullOrWhiteSpace(EnteredOnBehalfOfCompId))
            {
                StatusMessage = "Please enter FIX ID (tag 115).";
                return;
            }

            IsProcessing = true;
            ShowResults = false;
            ProcessProgress = 0;
            ProcessedTradesCount = 0;
            SentAllocationsCount = 0;
            SuccessfulAllocationsCount = 0;
            FailedAllocationsCount = 0;
            GeneratedMessages.Clear();
            StatusMessage = "Initializing...";
            ResultSuccessMessage = string.Empty;
            ResultFailureMessage = string.Empty;

            try
            {
                if (!selectedClient.HasValue)
                {
                    StatusMessage = "Please select a client and a file.";
                    return;
                }

                var clientId = selectedClient.Value.Key;
                var mapPath = Path.Combine(_mappingRepo.BaseDirectory, $"{clientId}_map.json");

                if (!File.Exists(mapPath))
                {
                    StatusMessage = $"Mapping file not found for client {clientId}";
                    return;
                }

                // Load mapping
                StatusMessage = "Loading mapping configuration...";
                _logger.LogInformation("Loading mapping from {MapPath}", mapPath);
                var mapping = _selectedMapping ?? MappingConfig.Load(mapPath);
                if (RequiresOnBehalfOfCompId)
                {
                    mapping.Predefined ??= new PredefinedFields();
                    mapping.Predefined.OnBehalfOfCompID = EnteredOnBehalfOfCompId.Trim();
                }
                _selectedMapping = mapping;
                ProcessProgress = 10;

                // Parse spreadsheet
                StatusMessage = "Parsing spreadsheet...";
                var trades = _excelParser.Parse(SelectedFilePath, mapping);
                var totalTrades = trades.Count;
                AllocationsSubmittedCount = totalTrades;
                ProcessedTradesCount = totalTrades;
                ProcessProgress = 30;
                SuccessfulAllocationsCount = 0;
                FailedAllocationsCount = 0;

                if (trades.Count == 0)
                {
                    StatusMessage = "No trade records found in spreadsheet.";
                    ShowResults = true;
                    return;
                }

                string? GetColumn(int tag) => mapping.TradeAllocations
                    .FirstOrDefault(kvp => FixValueNormalizer.TryParseTagNumber(kvp.Value, out var parsed) && parsed == tag)
                    .Key;

                var symbolColumn = GetColumn(55);
                var sideColumn = GetColumn(54);
                var securityIdColumn = GetColumn(48);
                var accountColumn = GetColumn(79);
                var qtyColumn = GetColumn(80);
                var priceColumn = GetColumn(153);
                var tradeDateColumn = GetColumn(75) ?? "TRADEDATE";
                var hasSecurityIdMapping = !string.IsNullOrWhiteSpace(securityIdColumn);
                var requiresSymbol = !hasSecurityIdMapping;

                string GetFieldValue(TradeRecord trade, string? column)
                {
                    if (string.IsNullOrWhiteSpace(column)) return string.Empty;
                    return trade.Fields.TryGetValue(column, out var val) ? val.Trim() : string.Empty;
                }

                bool HasValue(TradeRecord trade, string? column) =>
                    !string.IsNullOrWhiteSpace(GetFieldValue(trade, column));

                if (requiresSymbol && string.IsNullOrWhiteSpace(symbolColumn))
                {
                    var message = "Missing required mapping for Symbol (tag 55).";
                    StatusMessage = message;
                    ResultFailureMessage = message;
                    ResultMessage = message;
                    ShowResults = true;
                    ProcessProgress = 100;
                    return;
                }

                int skippedInvalidEquities = 0;
                string? invalidEquityMessage = null;
                if (hasSecurityIdMapping)
                {
                    var filteredTrades = trades.Where(t => HasValue(t, securityIdColumn)).ToList();
                    skippedInvalidEquities = trades.Count - filteredTrades.Count;
                    if (skippedInvalidEquities > 0)
                    {
                        invalidEquityMessage = $"{skippedInvalidEquities} allocation(s) with invalid equities have been skipped.";
                        trades = filteredTrades;
                        ProcessedTradesCount = trades.Count;
                    }
                }

                var eligibleTrades = Math.Max(totalTrades - skippedInvalidEquities, 0);

                string EnsureInvalidEquityNotice(string message)
                {
                    if (string.IsNullOrWhiteSpace(invalidEquityMessage))
                    {
                        return message;
                    }

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return invalidEquityMessage;
                    }

                    return message.Contains(invalidEquityMessage, StringComparison.OrdinalIgnoreCase)
                        ? message
                        : $"{message} {invalidEquityMessage}";
                }

                if (trades.Count == 0)
                {
                    var message = EnsureInvalidEquityNotice("No valid allocations to process.");
                    StatusMessage = message;
                    ResultSuccessMessage = message;
                    ResultFailureMessage = string.Empty;
                    ResultMessage = message;
                    ProcessedTradesCount = 0;
                    ShowResults = true;
                    ProcessProgress = 100;
                    return;
                }

                var numericIssues = FixValueNormalizer.FindNumericFieldIssues(trades, mapping);
                if (numericIssues.Count > 0)
                {
                    var detail = string.Join(", ",
                        numericIssues.Select(issue =>
                            $"{FixValueNormalizer.FormatColumnLabel(issue.ColumnKey)} (tag {issue.Tag}) = '{issue.SampleValue}'"));
                    var message = EnsureInvalidEquityNotice($"Unexpected non-numeric value(s) found for numeric FIX fields: {detail}.");
                    StatusMessage = message;
                    ResultFailureMessage = message;
                    ResultMessage = message;
                    FailedAllocationsCount = eligibleTrades;
                    ProcessedTradesCount = 0;
                    ShowResults = true;
                    ProcessProgress = 100;
                    return;
                }

                string ResolveSymbolValue(TradeRecord trade)
                {
                    var symbolValue = GetFieldValue(trade, symbolColumn);
                    if (!string.IsNullOrWhiteSpace(symbolValue))
                    {
                        return symbolValue;
                    }

                    return hasSecurityIdMapping ? "NA" : string.Empty;
                }

                string ResolveGroupKey(TradeRecord trade)
                {
                    var sideValue = GetFieldValue(trade, sideColumn);
                    if (hasSecurityIdMapping)
                    {
                        var secIdValue = GetFieldValue(trade, securityIdColumn);
                        if (!string.IsNullOrWhiteSpace(secIdValue))
                        {
                            return $"48:{secIdValue}|54:{sideValue}";
                        }
                    }

                    var symbolValue = ResolveSymbolValue(trade);
                    return $"55:{symbolValue}|54:{sideValue}";
                }

                string ResolveGroupDisplay(TradeRecord trade)
                {
                    if (hasSecurityIdMapping)
                    {
                        var secIdValue = GetFieldValue(trade, securityIdColumn);
                        if (!string.IsNullOrWhiteSpace(secIdValue))
                        {
                            return secIdValue;
                        }
                    }

                    return ResolveSymbolValue(trade);
                }

                bool IsMissing(TradeRecord trade, string? column, bool required = true) =>
                    required && !HasValue(trade, column);

                var missingTrades = trades
                    .Where(t =>
                        IsMissing(t, symbolColumn, requiresSymbol) ||
                        IsMissing(t, sideColumn) ||
                        IsMissing(t, accountColumn) ||
                        IsMissing(t, qtyColumn) ||
                        IsMissing(t, priceColumn))
                    .ToList();

                var missingTags = new List<string>();
                if (missingTrades.Any(t => IsMissing(t, sideColumn))) missingTags.Add("Side (tag 54)");
                if (requiresSymbol && missingTrades.Any(t => IsMissing(t, symbolColumn))) missingTags.Add("Symbol (tag 55)");
                if (missingTrades.Any(t => IsMissing(t, accountColumn))) missingTags.Add("AllocAccount (tag 79)");
                if (missingTrades.Any(t => IsMissing(t, qtyColumn))) missingTags.Add("AllocShares (tag 80)");
                if (missingTrades.Any(t => IsMissing(t, priceColumn))) missingTags.Add("AllocAvgPx (tag 153)");

                string? missingRequiredMessage = null;
                int missingCount = missingTrades.Count;
                if (missingTrades.Any())
                {
                    FailedAllocationsCount = missingCount;
                    missingRequiredMessage = EnsureInvalidEquityNotice(
                        $"{missingCount} out of {totalTrades} Allocation(s) failed to process. Missing required value(s): {string.Join(", ", missingTags)}.");
                    StatusMessage = missingRequiredMessage;
                    ResultFailureMessage = missingRequiredMessage;
                    ResultMessage = missingRequiredMessage;
                    _logger.LogWarning(missingRequiredMessage);
                }

                var validTrades = trades.Where(t => !missingTrades.Contains(t)).ToList();
                ProcessedTradesCount = validTrades.Count;

                if (validTrades.Count == 0)
                {
                    SuccessfulAllocationsCount = 0;
                    ShowResults = true;
                    ProcessProgress = 100;
                    ResultSuccessMessage = string.Empty;
                    ResultFailureMessage = EnsureInvalidEquityNotice(missingRequiredMessage ?? "No valid allocations to process.");
                    ResultMessage = ResultFailureMessage;
                    return;
                }

                // Setup FIX engine
                StatusMessage = "Initializing FIX engine...";
                var senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "TRADEALLOC";
                var targetComp = mapping.Predefined?.TargetCompID ?? "EXECUTOR";
                var senderSubId = mapping.Predefined?.SenderSubID;
                var targetSubId = mapping.Predefined?.TargetSubID;

                _fixEngine.AppendSessionsIfMissing(new[] { (senderComp, targetComp, senderSubId, targetSubId) });
                _fixEngine.ReloadSettings(_fixApp);

                if (!IsDryRun)
                {
                    _fixEngine.Start();
                    await Task.Delay(500); // Give engine time to start
                }

                ProcessProgress = 50;

                // Group and process trades
                StatusMessage = "Processing trades...";
                var groupedBySymbolAndSide = validTrades
                    .GroupBy(ResolveGroupKey)
                    .ToList();
                var preparedMessages = new List<(Message Message, string AllocId, string Symbol, string Side, string GroupKey, int TradeCount, IEnumerable<TradeRecord> Trades)>();
                int failedFixMessages = 0;
                int successfulFixMessages = 0;
                int buildFailures = 0;
                var reportEntries = new List<ValidationResult>();
                var groupErrors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var allocIdToGroupKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var groupTotals = trades
                    .GroupBy(ResolveGroupKey)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var groupMeta = trades
                    .GroupBy(ResolveGroupKey)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                        {
                            var first = g.First();
                            return new
                            {
                                Side = GetFieldValue(first, sideColumn),
                                Symbol = ResolveSymbolValue(first),
                                Display = ResolveGroupDisplay(first)
                            };
                        },
                        StringComparer.OrdinalIgnoreCase);
                var missingTagsByGroup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                void AddGroupError(string groupKey, string error)
                {
                    if (string.IsNullOrWhiteSpace(error)) return;
                    if (!groupErrors.TryGetValue(groupKey, out var errors))
                    {
                        errors = new List<string>();
                        groupErrors[groupKey] = errors;
                    }
                    errors.Add(error);
                }

                static string NormalizeTradeDate(string? raw)
                {
                    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                    var trimmed = raw.Trim();
                    if (trimmed.Length == 8 && trimmed.All(char.IsDigit))
                    {
                        return trimmed;
                    }

                    if (DateTime.TryParse(trimmed, out var parsed))
                    {
                        return parsed.ToString("yyyyMMdd");
                    }

                    return string.Empty;
                }

                static string ResolveTradeDate(IEnumerable<TradeRecord> groupTrades, string tradeDateColumnName)
                {
                    foreach (var trade in groupTrades)
                    {
                        if (trade.Fields.TryGetValue(tradeDateColumnName, out var raw))
                        {
                            var normalized = NormalizeTradeDate(raw);
                            if (!string.IsNullOrEmpty(normalized))
                            {
                                return normalized;
                            }
                        }
                    }

                    return string.Empty;
                }

                static string FormatAllocId(string allocId, string tradeDate)
                {
                    return string.IsNullOrWhiteSpace(tradeDate) ? allocId : $"{allocId}_{tradeDate}";
                }

                foreach (var trade in missingTrades)
                {
                    var key = ResolveGroupKey(trade);

                    if (!missingTagsByGroup.TryGetValue(key, out var tags))
                    {
                        tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        missingTagsByGroup[key] = tags;
                    }

                    if (IsMissing(trade, sideColumn)) tags.Add("Side (tag 54)");
                    if (requiresSymbol && IsMissing(trade, symbolColumn)) tags.Add("Symbol (tag 55)");
                    if (IsMissing(trade, accountColumn)) tags.Add("AllocAccount (tag 79)");
                    if (IsMissing(trade, qtyColumn)) tags.Add("AllocShares (tag 80)");
                    if (IsMissing(trade, priceColumn)) tags.Add("AllocAvgPx (tag 153)");
                }

                foreach (var kvp in missingTagsByGroup)
                {
                    var groupTotal = groupTotals.TryGetValue(kvp.Key, out var count) ? count : 0;
                    var meta = groupMeta.TryGetValue(kvp.Key, out var details)
                        ? details
                        : new { Side = string.Empty, Symbol = string.Empty, Display = string.Empty };
                    var message =
                        $"{groupTotal} out of {AllocationsSubmittedCount} allocations identified for group trade {meta.Side}/{meta.Display} failed to merge because it is missing required value(s): {string.Join(", ", kvp.Value)}. Allocations processing cancelled.";
                    AddGroupError(kvp.Key, message);
                }

                var fixBuilderLogger = _loggerFactory.CreateLogger<FixMessageBuilder>();

                var fixBuilder = new FixMessageBuilder(mapping, _appConfig.Fix, fixBuilderLogger);

                int groupIndex = 0;
                foreach (var symbolGroup in groupedBySymbolAndSide)
                {
                    var groupKey = symbolGroup.Key;
                    var meta = groupMeta.TryGetValue(groupKey, out var details)
                        ? details
                        : new { Side = string.Empty, Symbol = string.Empty, Display = string.Empty };
                    string symbol = meta.Symbol;
                    string side = meta.Side;
                    var allocId = fixBuilder.NextAllocId();
                    var groupTradeDate = ResolveTradeDate(symbolGroup, tradeDateColumn);
                    var reportAllocId = FormatAllocId(allocId, groupTradeDate);
                    allocIdToGroupKey[allocId] = groupKey;

                    StatusMessage = $"Building message for {side} / {meta.Display} ({symbolGroup.Count()} trades)...";

                    Message mergedMsg;
                    try
                    {
                        mergedMsg = fixBuilder.BuildFromAllocGroup(allocId, senderComp, targetComp, symbolGroup, mapping);
                    }
                    catch (Exception ex)
                    {
                        var errorMessage = $"Failed to build FIX allocation for symbol '{symbol}': {ex.Message}";
                        StatusMessage = errorMessage;
                        FailedAllocationsCount += symbolGroup.Count();
                        buildFailures++;
                        GeneratedMessages.Add(new FixMessageResult
                        {
                            AllocId = allocId,
                            Symbol = symbol,
                            TradeCount = symbolGroup.Count(),
                            RawFix = string.Empty,
                            Status = "Failed",
                            ErrorMessage = ex.Message,
                            Timestamp = DateTime.UtcNow
                        });
                        reportEntries.Add(new ValidationResult(
                            DateTime.UtcNow.ToString("o"),
                            mapping.ClientId ?? string.Empty,
                            reportAllocId,
                            side,
                            symbol,
                            "Failed",
                            string.Empty,
                            string.Empty
                        ));
                        AddGroupError(groupKey, ex.Message);
                        _logger.LogWarning(ex, "Failed to build FIX allocation for symbol {Symbol}", symbol);
                        continue;
                    }

                    preparedMessages.Add((mergedMsg, allocId, symbol, side, groupKey, symbolGroup.Count(), symbolGroup));

                    groupIndex++;
                    ProcessProgress = 50 + (groupIndex * 50 / groupedBySymbolAndSide.Count);
                }

                bool canSendFixMessages = !IsDryRun && !missingTrades.Any() && buildFailures == 0;

                foreach (var prepared in preparedMessages)
                {
                    var mergedMsg = prepared.Message;
                    var allocId = prepared.AllocId;
                    var symbol = prepared.Symbol;
                    var side = prepared.Side;
                    var tradeCount = prepared.TradeCount;
                    var groupTrades = prepared.Trades;
                    var groupTradeDate = ResolveTradeDate(groupTrades, tradeDateColumn);
                    var groupKey = prepared.GroupKey;

                    // Find session
                    SessionID? sessionID = null;
                    if ((IsDryRun || canSendFixMessages) && _fixEngine.SessionSettings != null)
                    {
                        foreach (var sid in _fixEngine.SessionSettings.GetSessions())
                        {
                            if (string.Equals(sid.SenderCompID, senderComp, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(sid.TargetCompID, targetComp, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrWhiteSpace(senderSubId) &&
                                    !string.Equals(sid.SenderSubID, senderSubId, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                if (!string.IsNullOrWhiteSpace(targetSubId) &&
                                    !string.Equals(sid.TargetSubID, targetSubId, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                sessionID = sid;
                                break;
                            }
                        }
                    }

                    if ((IsDryRun || canSendFixMessages) && sessionID is null)
                    {
                        StatusMessage = BuildSessionMissingMessage(senderComp, targetComp, senderSubId, targetSubId);
                        FailedAllocationsCount += tradeCount;
                        GeneratedMessages.Add(new FixMessageResult
                        {
                            AllocId = allocId,
                            Symbol = symbol,
                            TradeCount = tradeCount,
                            RawFix = mergedMsg.ToString().Replace('\u0001', '|'),
                            Status = "Failed",
                            ErrorMessage = BuildSessionMissingMessage(senderComp, targetComp, senderSubId, targetSubId),
                            Timestamp = DateTime.UtcNow
                        });
                        AddGroupError(groupKey, BuildSessionMissingMessage(senderComp, targetComp, senderSubId, targetSubId));
                        failedFixMessages++;
                        continue;
                    }

                    // Send message
                    string sendResult;
                    if (IsDryRun)
                    {
                        var validationErrors = _fixClient.ValidateAllocation(mergedMsg);
                        if (validationErrors.Count > 0)
                        {
                            sendResult = string.Join("; ", validationErrors);
                        }
                        else
                        {
                            sendResult = "OK";
                        }
                    }
                    else if (!canSendFixMessages)
                    {
                        sendResult = "Skipped";
                        if (!groupErrors.ContainsKey(groupKey))
                        {
                            var groupTotal = groupTotals.TryGetValue(groupKey, out var count) ? count : tradeCount;
                            var failedAllocationsCount = totalTrades - groupTotal;
                            var cancelMessage =
                                $"{groupTotal} out of {AllocationsSubmittedCount} allocations identified for group trade {side}/{symbol} were successfully merged. Fix message cannot be sent because {failedAllocationsCount} trades have error(s). Allocations processing cancelled.";
                            AddGroupError(groupKey, cancelMessage);
                        }
                    }
                    else
                    {
                        sendResult = await _fixClient.SendAsync(mergedMsg, sessionID);
                    }
                    if (sendResult != "OK")
                    {
                        failedFixMessages++;
                        if (!string.Equals(sendResult, "Skipped", StringComparison.OrdinalIgnoreCase))
                        {
                            AddGroupError(groupKey, sendResult);
                        }
                    }
                    else if (!IsDryRun)
                    {
                        successfulFixMessages++;
                    }

                    // Track allocations successfully merged into FIX (per trade row in the group)
                    if (sendResult == "OK")
                    {
                        SuccessfulAllocationsCount += tradeCount;
                    }
                    else
                    {
                        FailedAllocationsCount += tradeCount;
                    }

                    if (!IsDryRun && sendResult == "OK")
                    {
                        SentAllocationsCount += tradeCount;
                    }

                    // Record result
                    reportEntries.Add(new ValidationResult(
                        DateTime.UtcNow.ToString("o"),
                        mapping.ClientId ?? string.Empty,
                        FormatAllocId(allocId,
                            mergedMsg.IsSetField(75)
                                ? NormalizeTradeDate(mergedMsg.GetString(75))
                                : groupTradeDate),
                        side,
                        symbol,
                        sendResult == "OK" ? "Sent" : "Failed",
                        string.Empty,
                        mergedMsg.ToString().Replace('\u0001', '|')
                    ));

                    GeneratedMessages.Add(new FixMessageResult
                    {
                        AllocId = allocId,
                        Symbol = symbol,
                        Side = side,
                        TradeCount = tradeCount,
                        RawFix = mergedMsg.ToString().Replace('\u0001', '|'),
                        Status = sendResult == "OK" ? "Sent" : "Failed",
                        ErrorMessage = sendResult == "OK" ? string.Empty : sendResult,
                        Timestamp = DateTime.UtcNow
                    });
                }

                if (!IsDryRun)
                {
                    _fixEngine.Stop();
                }

                // Normalize success count to remaining allocations if failures were tallied separately
                SuccessfulAllocationsCount = Math.Max(eligibleTrades - FailedAllocationsCount, SuccessfulAllocationsCount);

                ProcessProgress = 100;
                var successMessage = $"{validTrades.Count} out of {AllocationsSubmittedCount} allocations successfully processed and merged across {groupedBySymbolAndSide.Count} allocation group(s).";
                if (!IsDryRun && successfulFixMessages > 0)
                {
                    successMessage = $"{successMessage} {successfulFixMessages} Fix message(s) successfully sent to {targetComp}.";
                }
                string? fixSendFailureMessage = null;
                if (!IsDryRun && failedFixMessages > 0)
                {
                    fixSendFailureMessage = $"{failedFixMessages} Fix message(s) failed to send.";
                }
                var groupErrorSummary = groupErrors.Count > 0
                    ? string.Join(" | ", groupErrors.Values.SelectMany(v => v).Distinct())
                    : null;
                var failureMessage = string.Join(" ", new[] { missingRequiredMessage, fixSendFailureMessage, groupErrorSummary }
                    .Where(m => !string.IsNullOrWhiteSpace(m)));
                if (string.IsNullOrWhiteSpace(failureMessage))
                {
                    successMessage = EnsureInvalidEquityNotice(successMessage);
                }
                else
                {
                    failureMessage = EnsureInvalidEquityNotice(failureMessage);
                }
                ResultSuccessMessage = successMessage;
                ResultFailureMessage = failureMessage;
                StatusMessage = string.IsNullOrEmpty(ResultFailureMessage) ? ResultSuccessMessage : ResultFailureMessage;
                ResultMessage = StatusMessage;
                ShowResults = true;
                if (!IsDryRun && failedFixMessages == 0 && successfulFixMessages > 0)
                {
                    SelectedFilePath = null;
                }

                var hasProcessingErrors = missingTrades.Any() || buildFailures > 0 || failedFixMessages > 0 || groupErrors.Count > 0;
                string ResolveErrorDetails(ValidationResult entry)
                {
                    if (groupErrors.Count == 0)
                    {
                        return string.Empty;
                    }

                    if (!allocIdToGroupKey.TryGetValue(entry.AllocID, out var entryGroupKey))
                    {
                        return string.Empty;
                    }

                    if (!groupErrors.TryGetValue(entryGroupKey, out var errors))
                    {
                        return string.Empty;
                    }

                    var details = errors
                        .Distinct()
                        .ToList();
                    return details.Count > 0 ? string.Join(" | ", details) : string.Empty;
                }

                if (IsDryRun)
                {
                    var dryRunStatus = hasProcessingErrors ? "Failed" : "Valid. Not Sent";
                    foreach (var entry in reportEntries)
                    {
                        var errorDetails = ResolveErrorDetails(entry);
                        var sideDisplay = FixValueNormalizer.FormatSideDisplay(entry.Side);
                        _validationReport.Add(entry with
                        {
                            Side = sideDisplay,
                            ProcessingStatus = dryRunStatus,
                            ErrorDetails = errorDetails
                        });
                    }

                    foreach (var message in GeneratedMessages)
                    {
                        message.Status = dryRunStatus;
                        message.ErrorMessage = hasProcessingErrors ? ResultFailureMessage : string.Empty;
                    }
                }
                else if (reportEntries.Count > 0)
                {
                    foreach (var entry in reportEntries)
                    {
                        var errorDetails = ResolveErrorDetails(entry);
                        var sideDisplay = FixValueNormalizer.FormatSideDisplay(entry.Side);
                        _validationReport.Add(entry with { Side = sideDisplay, ErrorDetails = errorDetails });
                    }
                }

                _validationReport.Save();

                if (IsDryRun && !hasProcessingErrors)
                {
                    TryUpdateMappingValidatedDate(mapPath);
                }

                _logger.LogInformation(ResultMessage);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                ResultMessage = ex.ToString();
                ShowResults = true;
                _logger.LogError(ex, "Error processing allocations");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void TryUpdateMappingValidatedDate(string mapPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
                {
                    return;
                }

                var previousWriteTime = File.GetLastWriteTime(mapPath);
                var mapping = MappingConfig.Load(mapPath);
                mapping.DateValidated = DateTime.Now.ToString("M/d/yyyy h:mm tt");

                var json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                File.WriteAllText(mapPath, json);
                File.SetLastWriteTime(mapPath, previousWriteTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update DateValidated for {MapPath}", mapPath);
            }
        }

        private bool CanProcessFile()
        {
            if (!SelectedClient.HasValue || string.IsNullOrEmpty(SelectedFilePath) || IsProcessing)
            {
                return false;
            }

            return !RequiresOnBehalfOfCompId || !string.IsNullOrWhiteSpace(EnteredOnBehalfOfCompId);
        }

        private bool CanDiscardForm()
        {
            if (IsProcessing)
            {
                return false;
            }

            return SelectedClient.HasValue || !string.IsNullOrEmpty(SelectedFilePath);
        }

        private void Discard()
        {
            if (IsProcessing)
            {
                return;
            }

            SelectedClient = null;
            SelectedFilePath = null;
            EnteredOnBehalfOfCompId = string.Empty;
            IsDryRun = true;
            ShowResults = false;
            ProcessProgress = 0;
            ProcessedTradesCount = 0;
            SentAllocationsCount = 0;
            AllocationsSubmittedCount = 0;
            SuccessfulAllocationsCount = 0;
            FailedAllocationsCount = 0;
            GeneratedMessages.Clear();
            StatusMessage = "Ready";
            ResultSuccessMessage = string.Empty;
            ResultFailureMessage = string.Empty;
            ResultMessage = string.Empty;
            NotifyDiscardChanged();
        }

        private void ViewLogs()
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            if (Directory.Exists(logsDir))
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = logsDir
                });
            }
        }

        private static string BuildSessionMissingMessage(
            string senderComp,
            string targetComp,
            string? senderSubId,
            string? targetSubId)
        {
            var parts = new List<string> { $"{senderComp} -> {targetComp}" };
            if (!string.IsNullOrWhiteSpace(senderSubId))
            {
                parts.Add($"SenderSubID={senderSubId}");
            }
            if (!string.IsNullOrWhiteSpace(targetSubId))
            {
                parts.Add($"TargetSubID={targetSubId}");
            }

            return "No FIX session found for " + string.Join(" ", parts);
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void NotifyCanProcessChanged()
        {
            OnPropertyChanged(nameof(CanProcess));
            CommandManager.InvalidateRequerySuggested();
        }

        private void NotifyDiscardChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
