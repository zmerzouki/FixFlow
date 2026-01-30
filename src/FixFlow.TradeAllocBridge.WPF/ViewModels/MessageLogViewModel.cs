using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace FixFlow.TradeAllocBridge.WPF.ViewModels
{
    public sealed class MessageLogViewModel : INotifyPropertyChanged
    {
        private const string DirectIngestionKey = "Direct";
        private const string EmailAutomationKey = "Email";

        private LogSourceOption? _selectedSource;
        private string _statusMessage = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<LogSourceOption> Sources { get; }
        public ObservableCollection<MessageLogEntry> Entries { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ExportCommand { get; }

        public MessageLogViewModel()
        {
            Sources = new ObservableCollection<LogSourceOption>
            {
                new LogSourceOption(DirectIngestionKey, "Direct Ingestion"),
                new LogSourceOption(EmailAutomationKey, "Email Automation")
            };

            Entries = new ObservableCollection<MessageLogEntry>();
            RefreshCommand = new RelayCommand(LoadEntries);
            ExportCommand = new RelayCommand(ExportEntries, () => Entries.Count > 0);

            SelectedSource = Sources.FirstOrDefault();
        }

        public LogSourceOption? SelectedSource
        {
            get => _selectedSource;
            set
            {
                if (_selectedSource != value)
                {
                    _selectedSource = value;
                    OnPropertyChanged(nameof(SelectedSource));
                    LoadEntries();
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

        public void CopyEntries(System.Collections.IEnumerable selectedItems)
        {
            var selected = selectedItems?.Cast<MessageLogEntry>().ToList() ?? new List<MessageLogEntry>();
            if (selected.Count == 0)
            {
                return;
            }

            var text = selected.Count == 1
                ? selected[0].RawFixMessage
                : string.Join(Environment.NewLine + Environment.NewLine, selected.Select(e => e.RawFixMessage));

            Clipboard.SetText(text);
            StatusMessage = $"Copied {selected.Count} message(s) to clipboard.";
        }

        private void LoadEntries()
        {
            Entries.Clear();

            var source = SelectedSource?.Key ?? DirectIngestionKey;
            var reportsDir = ResolveReportsDir(source);
            if (string.IsNullOrWhiteSpace(reportsDir) || !Directory.Exists(reportsDir))
            {
                StatusMessage = "Report folder not found.";
                return;
            }

            var files = Directory.GetFiles(reportsDir, "fix_validation_*.csv")
                .OrderBy(f => f)
                .ToList();

            if (files.Count == 0)
            {
                StatusMessage = "No report files found.";
                return;
            }

            var allEntries = new List<MessageLogEntry>();
            foreach (var file in files)
            {
                foreach (var entry in ParseReport(file, SelectedSource?.Name ?? "Direct Ingestion"))
                {
                    allEntries.Add(entry);
                }
            }

            foreach (var entry in allEntries.OrderByDescending(e => e.Timestamp))
            {
                Entries.Add(entry);
            }

            StatusMessage = $"Loaded {Entries.Count} message(s) from {files.Count} report file(s).";
            CommandManager.InvalidateRequerySuggested();
        }

        private static IEnumerable<MessageLogEntry> ParseReport(string path, string sourceName)
        {
            var lines = File.ReadLines(path).ToList();
            if (lines.Count <= 1)
            {
                yield break;
            }

            for (int i = 1; i < lines.Count; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = SplitCsv(line);
                if (fields.Count < 8)
                {
                    continue;
                }

                var timestampRaw = fields[0];
                DateTime.TryParse(timestampRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp);

                yield return new MessageLogEntry
                {
                    Timestamp = timestamp,
                    ClientId = fields[1],
                    AllocId = fields[2],
                    Side = fields[3],
                    Symbol = fields[4],
                    ProcessingStatus = fields[5],
                    ErrorDetails = fields[6],
                    RawFixMessage = fields[7],
                    Source = sourceName,
                    ReportFile = Path.GetFileName(path)
                };
            }
        }

        private static List<string> SplitCsv(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    continue;
                }

                if (c == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }

                sb.Append(c);
            }

            result.Add(sb.ToString());
            return result;
        }

        private static string? ResolveReportsDir(string sourceKey)
        {
            if (sourceKey == DirectIngestionKey)
            {
                return Path.Combine(AppContext.BaseDirectory, "reports");
            }

            var env = Environment.GetEnvironmentVariable("FIXFLOW_CLI_REPORTS");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            {
                return env;
            }

            var solutionRoot = FindAncestorWithFile(AppContext.BaseDirectory, "*.sln", maxLevels: 8);
            if (string.IsNullOrWhiteSpace(solutionRoot))
            {
                return null;
            }

            var cliProj = Path.Combine(solutionRoot, "src", "FixFlow.TradeAllocBridge.CLI", "bin");
            if (!Directory.Exists(cliProj))
            {
                return null;
            }

            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var cfgDir = Path.Combine(cliProj, configuration);
                if (!Directory.Exists(cfgDir)) continue;

                foreach (var tfmDir in Directory.GetDirectories(cfgDir))
                {
                    var reportsDir = Path.Combine(tfmDir, "reports");
                    if (Directory.Exists(reportsDir))
                    {
                        return reportsDir;
                    }
                }
            }

            return null;
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
            }

            return null;
        }

        private void ExportEntries()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                FileName = $"message_log_{DateTime.Now:yyyyMMdd}.csv"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("DateTime,ClientID,AllocID,Side,Symbol,ProcessingStatus,ErrorDetails,RawFixMessage,Source,ReportFile");

            foreach (var entry in Entries)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    EscapeCsv(entry.Timestamp.ToString("o")),
                    EscapeCsv(entry.ClientId),
                    EscapeCsv(entry.AllocId),
                    EscapeCsv(entry.Side),
                    EscapeCsv(entry.Symbol),
                    EscapeCsv(entry.ProcessingStatus),
                    EscapeCsv(entry.ErrorDetails),
                    EscapeCsv(entry.RawFixMessage),
                    EscapeCsv(entry.Source),
                    EscapeCsv(entry.ReportFile)
                }));
            }

            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Exported {Entries.Count} message(s) to CSV.";
        }

        private static string EscapeCsv(string? value)
        {
            var normalized = value ?? string.Empty;
            return $"\"{normalized.Replace("\"", "\"\"")}\"";
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed record LogSourceOption(string Key, string Name);

    public sealed class MessageLogEntry
    {
        public DateTime Timestamp { get; init; }
        public string ClientId { get; init; } = string.Empty;
        public string AllocId { get; init; } = string.Empty;
        public string Side { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string ProcessingStatus { get; init; } = string.Empty;
        public string ErrorDetails { get; init; } = string.Empty;
        public string RawFixMessage { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string ReportFile { get; init; } = string.Empty;
    }
}
