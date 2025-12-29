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
using RJF.TradeAllocBridge.Core.Config;
using RJF.TradeAllocBridge.Core.Excel;
using RJF.TradeAllocBridge.Core.Fix;
using RJF.TradeAllocBridge.Core.Mapping;
using RJF.TradeAllocBridge.Core.Reporting;

namespace RJF.TradeAllocBridge.WPF.ViewModels
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
        private string _resultMessage = "";

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

        public string SuccessRate => ProcessedTradesCount > 0
            ? $"{(SentAllocationsCount * 100.0 / ProcessedTradesCount):F1}%"
            : "N/A";

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
            if (!SelectedClient.HasValue || string.IsNullOrEmpty(SelectedFilePath))
            {
                StatusMessage = "Please select a client and a file.";
                return;
            }

            IsProcessing = true;
            ShowResults = false;
            ProcessProgress = 0;
            ProcessedTradesCount = 0;
            SentAllocationsCount = 0;
            GeneratedMessages.Clear();
            StatusMessage = "Initializing...";

            try
            {
                var clientId = SelectedClient.Value.Key;
                var mapPath = Path.Combine(_mappingRepo.BaseDirectory, $"{clientId}_map.json");

                if (!File.Exists(mapPath))
                {
                    StatusMessage = $"Mapping file not found for client {clientId}";
                    return;
                }

                // Load mapping
                StatusMessage = "Loading mapping configuration...";
                var mapping = _selectedMapping ?? MappingConfig.Load(mapPath);
                _selectedMapping = mapping;
                ProcessProgress = 10;

                // Parse spreadsheet
                StatusMessage = "Parsing spreadsheet...";
                var trades = _excelParser.Parse(SelectedFilePath);
                ProcessedTradesCount = trades.Count;
                ProcessProgress = 30;

                if (trades.Count == 0)
                {
                    StatusMessage = "No trade records found in spreadsheet.";
                    ShowResults = true;
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
                var symbolColumn = mapping.TradeAllocations
                    .FirstOrDefault(kvp => kvp.Value == "55").Key ?? "SYMBOL";

                var groupedBySymbol = trades
                    .GroupBy(t => t.Fields.GetValueOrDefault(symbolColumn, "UNKNOWN").Trim())
                    .ToList();

                var fixBuilderLogger = _loggerFactory.CreateLogger<FixMessageBuilder>();

                var fixBuilder = new FixMessageBuilder(mapping, _appConfig.Fix, fixBuilderLogger);

                int groupIndex = 0;
                foreach (var symbolGroup in groupedBySymbol)
                {
                    string symbol = symbolGroup.Key;
                    var allocId = $"{fixBuilder.NextAllocId()}-{DateTime.UtcNow:yyyyMMdd}";

                    StatusMessage = $"Building message for {symbol} ({symbolGroup.Count()} trades)...";

                    var mergedMsg = fixBuilder.BuildFromAllocGroup(allocId, senderComp, targetComp, symbolGroup, mapping);

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
                    SentAllocationsCount += symbolGroup.Count();

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
                        TradeCount = symbolGroup.Count(),
                        RawFix = mergedMsg.ToString().Replace('\u0001', '|'),
                        Status = sendResult == "OK" ? "Sent" : "Failed",
                        ErrorMessage = sendResult == "OK" ? string.Empty : sendResult,
                        Timestamp = DateTime.Now
                    });

                    groupIndex++;
                    ProcessProgress = 50 + (groupIndex * 50 / groupedBySymbol.Count);
                }

                _validationReport.Save();

                if (!IsDryRun)
                {
                    _fixEngine.Stop();
                }

                ProcessProgress = 100;
                ResultMessage = $"Successfully processed {ProcessedTradesCount} trades across {groupedBySymbol.Count} symbol group(s). {SentAllocationsCount} allocation(s) sent.";
                StatusMessage = ResultMessage;
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
