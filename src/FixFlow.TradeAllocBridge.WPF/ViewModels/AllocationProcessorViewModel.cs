using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                    AvailableClients.Add(new KeyValuePair<string, string>(
                        mapping.ClientId,
                        $"{mapping.ClientId} ({mapping.SenderDomain})"
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
                    OnPropertyChanged(nameof(CanProcess));
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
                    OnPropertyChanged(nameof(CanProcess));
                }
            }
        }

        public string? SelectedFileName => Path.GetFileName(SelectedFilePath);

        public bool IsDryRun
        {
            get => _isDryRun;
            set
            {
                if (_isDryRun != value)
                {
                    _isDryRun = value;
                    OnPropertyChanged(nameof(IsDryRun));
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

        public bool CanProcess => SelectedClient.HasValue && !string.IsNullOrEmpty(SelectedFilePath) && !IsProcessing;

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
                _selectedMapping = mapping;
                ProcessProgress = 10;

                // Parse spreadsheet
                StatusMessage = "Parsing spreadsheet...";
                var trades = _excelParser.Parse(SelectedFilePath);
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

                var symbolColumn = mapping.TradeAllocations
                    .FirstOrDefault(kvp => kvp.Value == "55").Key ?? "SYMBOL";
                var sideColumn = mapping.TradeAllocations
                    .FirstOrDefault(kvp => kvp.Value == "54").Key ?? "SIDE";
                var accountColumn = mapping.TradeAllocations
                    .FirstOrDefault(kvp => kvp.Value == "79").Key ?? "ALLOC ACCOUNT";
                var qtyColumn = mapping.TradeAllocations
                    .FirstOrDefault(kvp => kvp.Value == "80").Key ?? "QUANTITY";
                var priceColumn = mapping.TradeAllocations
                    .FirstOrDefault(kvp => kvp.Value == "153").Key ?? "PRICE";

                bool IsMissing(TradeRecord t, string column) =>
                    !t.Fields.TryGetValue(column, out var val) || string.IsNullOrWhiteSpace(val?.Trim());

                var missingTrades = trades
                    .Where(t =>
                        IsMissing(t, symbolColumn) ||
                        IsMissing(t, sideColumn) ||
                        IsMissing(t, accountColumn) ||
                        IsMissing(t, qtyColumn) ||
                        IsMissing(t, priceColumn))
                    .ToList();

                var missingTags = new List<string>();
                if (missingTrades.Any(t => IsMissing(t, sideColumn))) missingTags.Add("Side (tag 54)");
                if (missingTrades.Any(t => IsMissing(t, symbolColumn))) missingTags.Add("Symbol (tag 55)");
                if (missingTrades.Any(t => IsMissing(t, accountColumn))) missingTags.Add("AllocAccount (tag 79)");
                if (missingTrades.Any(t => IsMissing(t, qtyColumn))) missingTags.Add("AllocShares (tag 80)");
                if (missingTrades.Any(t => IsMissing(t, priceColumn))) missingTags.Add("AllocAvgPx (tag 153)");

                string? missingRequiredMessage = null;
                int missingCount = missingTrades.Count;
                if (missingTrades.Any())
                {
                    FailedAllocationsCount = missingCount;
                    missingRequiredMessage = $"{missingCount} out of {totalTrades} Allocations have failed to process because it is missing required value(s): {string.Join(", ", missingTags)}";
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
                    ResultFailureMessage = missingRequiredMessage ?? "No valid allocations to process.";
                    return;
                }

                // Setup FIX engine
                StatusMessage = "Initializing FIX engine...";
                var senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "TRADEALLOC";
                var targetComp = mapping.Predefined?.TargetCompID ?? "EXECUTOR";

                _fixEngine.AppendSessionsIfMissing(new[] { (senderComp, targetComp) });
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
                    .GroupBy(t => new
                    {
                        Symbol = t.Fields.TryGetValue(symbolColumn, out var symbolValue) ? symbolValue.Trim() : string.Empty,
                        Side = t.Fields.TryGetValue(sideColumn, out var sideValue) ? sideValue.Trim() : string.Empty
                    })
                    .ToList();

                var fixBuilderLogger = _loggerFactory.CreateLogger<FixMessageBuilder>();

                var fixBuilder = new FixMessageBuilder(mapping, _appConfig.Fix, fixBuilderLogger);

                int groupIndex = 0;
                foreach (var symbolGroup in groupedBySymbolAndSide)
                {
                    var groupKey = symbolGroup.Key;
                    string symbol = groupKey.Symbol;
                    string side = groupKey.Side;
                    var allocId = fixBuilder.NextAllocId();

                    StatusMessage = $"Building message for {symbol} / {side} ({symbolGroup.Count()} trades)...";

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
                        GeneratedMessages.Add(new FixMessageResult
                        {
                            AllocId = allocId,
                            Symbol = symbol,
                            TradeCount = symbolGroup.Count(),
                            RawFix = string.Empty,
                            Status = "Failed",
                            ErrorMessage = ex.Message,
                            Timestamp = DateTime.Now
                        });
                        _validationReport.Add(new ValidationResult(
                            DateTime.Now.ToString("o"),
                            string.Join(",", symbolGroup.Select(t => t.Id)),
                            allocId,
                            symbol,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            "Failed",
                            ex.Message,
                            string.Empty
                        ));
                        _logger.LogWarning(ex, "Failed to build FIX allocation for symbol {Symbol}", symbol);
                        continue;
                    }

                    // Find session
                    SessionID? sessionID = null;
                    if (_fixEngine.SessionSettings != null)
                    {
                        foreach (var sid in _fixEngine.SessionSettings.GetSessions())
                        {
                            if (string.Equals(sid.SenderCompID, senderComp, StringComparison.OrdinalIgnoreCase))
                            {
                                sessionID = sid;
                                break;
                            }
                        }
                    }

                    if (sessionID is null)
                    {
                        StatusMessage = $"No FIX session found for {senderComp}";
                        FailedAllocationsCount += symbolGroup.Count();
                        GeneratedMessages.Add(new FixMessageResult
                        {
                            AllocId = allocId,
                            Symbol = symbol,
                            TradeCount = symbolGroup.Count(),
                            RawFix = mergedMsg.ToString().Replace('\u0001', '|'),
                            Status = "Failed",
                            ErrorMessage = $"No FIX session found for {senderComp}",
                            Timestamp = DateTime.Now
                        });
                        continue;
                    }

                    // Send message
                    var sendResult = IsDryRun ? "OK" : await _fixClient.SendAsync(mergedMsg, sessionID);

                    // Track allocations successfully merged into FIX (per trade row in the group)
                    if (sendResult == "OK")
                    {
                        SuccessfulAllocationsCount += symbolGroup.Count();
                    }
                    else
                    {
                        FailedAllocationsCount += symbolGroup.Count();
                    }

                    if (!IsDryRun && sendResult == "OK")
                    {
                        SentAllocationsCount += symbolGroup.Count();
                    }

                    // Record result
                    _validationReport.Add(new ValidationResult(
                        DateTime.Now.ToString("o"),
                        string.Join(",", symbolGroup.Select(t => t.Id)),
                        allocId,
                        symbol,
                        mergedMsg.IsSetField(80) ? mergedMsg.GetString(80) : "",
                        mergedMsg.IsSetField(153) ? mergedMsg.GetString(153) : "",
                        mergedMsg.IsSetField(79) ? mergedMsg.GetString(79) : "",
                        sendResult == "OK" ? "Sent" : "Failed",
                        sendResult == "OK" ? "" : sendResult,
                        mergedMsg.ToString().Replace('\u0001', '|')
                    ));

                    GeneratedMessages.Add(new FixMessageResult
                        {
                            AllocId = allocId,
                            Symbol = symbol,
                            Side = side,
                            TradeCount = symbolGroup.Count(),
                            RawFix = mergedMsg.ToString().Replace('\u0001', '|'),
                            Status = sendResult == "OK" ? "Sent" : "Failed",
                        ErrorMessage = sendResult == "OK" ? string.Empty : sendResult,
                        Timestamp = DateTime.Now
                    });

                    groupIndex++;
                    ProcessProgress = 50 + (groupIndex * 50 / groupedBySymbolAndSide.Count);
                }

                _validationReport.Save();

                if (!IsDryRun)
                {
                    _fixEngine.Stop();
                }

                // Normalize success count to remaining allocations if failures were tallied separately
                SuccessfulAllocationsCount = Math.Max(AllocationsSubmittedCount - FailedAllocationsCount, SuccessfulAllocationsCount);

                ProcessProgress = 100;
                var successMessage = $"{validTrades.Count} out of {AllocationsSubmittedCount} allocations have successfully processed and merged across {groupedBySymbolAndSide.Count} symbol/side group(s).";
                ResultSuccessMessage = successMessage;
                ResultFailureMessage = missingRequiredMessage ?? string.Empty;
                StatusMessage = string.IsNullOrEmpty(ResultFailureMessage) ? successMessage : ResultFailureMessage;
                ResultMessage = StatusMessage;
                ShowResults = true;

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

        private bool CanProcessFile()
        {
            return SelectedClient.HasValue && !string.IsNullOrEmpty(SelectedFilePath) && !IsProcessing;
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

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
