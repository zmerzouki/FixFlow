using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FixFlow.TradeAllocBridge.CLI.Services
{
    /// <summary>
    /// Watches an "incoming" staging folder for new mapping files and atomically moves
    /// them into the active configs folder once the incoming files are stable/complete.
    /// Designed to be robust against writers that take time to create files and transient locks.
    /// </summary>
    public sealed class MappingStagingWatcher : IAsyncDisposable
    {
        private readonly string _incomingDir;
        private readonly string _activeConfigsDir;
        private readonly ILogger<MappingStagingWatcher> _logger;
        private readonly FileSystemWatcher _fsw;
        private readonly Channel<string> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _processorTask;

        /// <summary>
        /// Optional callback invoked after a file was successfully moved into the active configs dir.
        /// </summary>
        public Action<string>? OnMappingDeployed { get; set; }

        public MappingStagingWatcher(string incomingDir, string activeConfigsDir, ILogger<MappingStagingWatcher> logger)
        {
            _incomingDir = Path.GetFullPath(incomingDir);
            _activeConfigsDir = Path.GetFullPath(activeConfigsDir);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            Directory.CreateDirectory(_incomingDir);
            Directory.CreateDirectory(_activeConfigsDir);

            _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            _fsw = new FileSystemWatcher(_incomingDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            _fsw.Created += OnFsEvent;
            _fsw.Changed += OnFsEvent;
            _fsw.Renamed += OnFsRenamed;

            // Start background processor
            _processorTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
        }

        private void OnFsEvent(object? sender, FileSystemEventArgs e)
        {
            EnqueueWithDebounce(e.FullPath);
        }

        private void OnFsRenamed(object? sender, RenamedEventArgs e)
        {
            EnqueueWithDebounce(e.FullPath);
        }

        // Debounce/guarding: schedule enqueue after a short delay so writers can finish.
        private void EnqueueWithDebounce(string path)
        {
            // if incoming file uses .tmp convention, ignore until final .json (optional)
            // If you use a .tmp write-then-rename convention from producer, this guards double events.
            _ = Task.Run(async () =>
            {
                try
                {
                    // short delay so that a single create/rename finishes writing
                    await Task.Delay(350, _cts.Token).ConfigureAwait(false);
                    if (!_queue.Writer.TryWrite(path))
                    {
                        _logger.LogDebug("Failed to enqueue mapping {File} for processing", path);
                    }
                }
                catch (OperationCanceledException) { /* shutting down */ }
            });
        }

        // Call on startup to process any existing files already present in the incoming folder.
        public void EnqueueExisting()
        {
            foreach (var f in Directory.EnumerateFiles(_incomingDir, "*.json"))
            {
                _queue.Writer.TryWrite(f);
            }
        }

        private async Task ProcessQueueAsync(CancellationToken ct)
        {
            var reader = _queue.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var path))
                {
                    try
                    {
                        await ProcessFileAsync(path, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error processing mapping staging file {Path}", path);
                    }
                }
            }
        }

        private async Task ProcessFileAsync(string incomingPath, CancellationToken ct)
        {
            // file may have been removed/renamed already
            if (!File.Exists(incomingPath))
            {
                _logger.LogDebug("Staging file no longer exists: {Path}", incomingPath);
                return;
            }

            var fileName = Path.GetFileName(incomingPath);
            var destPath = Path.Combine(_activeConfigsDir, fileName);

            _logger.LogInformation("Processing staged mapping {File}", fileName);

            // Try multiple times to move/replace; writers may still have handles.
            const int maxAttempts = 8;
            const int baseDelayMs = 300;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // If destination exists, prefer atomic Replace (keeps dest backup null)
                    if (File.Exists(destPath))
                    {
                        // Move incoming to a temporary filename next to destination, then Replace
                        var tmpDest = Path.Combine(_activeConfigsDir, $"{fileName}.{Guid.NewGuid():N}.tmp");
                        File.Move(incomingPath, tmpDest); // may throw if file locked
                        File.Replace(tmpDest, destPath, null); // may throw if dest locked
                    }
                    else
                    {
                        // Simple move is atomic on same volume
                        File.Move(incomingPath, destPath);
                    }

                    _logger.LogInformation("Deployed mapping {File} -> {Dest}", fileName, destPath);
                    OnMappingDeployed?.Invoke(destPath);
                    return;
                }
                catch (IOException ioEx)
                {
                    _logger.LogWarning(ioEx, "Attempt {Attempt} to move staged mapping {File} failed (IO). Retrying...", attempt, fileName);
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    _logger.LogWarning(uaEx, "Attempt {Attempt} to move staged mapping {File} failed (Access). Retrying...", attempt, fileName);
                }
                catch (Exception ex)
                {
                    // Non-transient
                    _logger.LogError(ex, "Failed to deploy staged mapping {File}", fileName);
                    break;
                }

                // If this was last attempt, log and leave file in incoming for manual inspection
                if (attempt == maxAttempts)
                {
                    _logger.LogError("Giving up moving staged mapping {File}. File remains in {Incoming}", fileName, _incomingDir);
                    return;
                }

                // exponential backoff
                await Task.Delay(baseDelayMs * attempt, ct).ConfigureAwait(false);
            }
        }

        public ValueTask StopAsync()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            _fsw.EnableRaisingEvents = false;
            _fsw.Created -= OnFsEvent;
            _fsw.Changed -= OnFsEvent;
            _fsw.Renamed -= OnFsRenamed;

            return new ValueTask(_processorTask); // caller can await _processorTask if desired
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _fsw.Dispose();
            _cts.Dispose();
        }
    }
}       