using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace FixFlow.TradeAllocBridge.CLI.Services
{
    public sealed class AllocationProcessingWorker : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<AllocationProcessingWorker> _logger;
        private readonly int _intervalSeconds;
        private readonly bool _dryRun;

        public AllocationProcessingWorker(
            IServiceProvider services,
            int intervalSeconds,
            bool dryRun)
        {
            _services = services;
            _intervalSeconds = intervalSeconds;
            _dryRun = dryRun;
            _logger = services.GetRequiredService<ILogger<AllocationProcessingWorker>>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Allocation processing worker started. Interval: {IntervalSeconds}s", _intervalSeconds);

            await RunOnceSafelyAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_intervalSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceSafelyAsync(stoppingToken);
            }
        }

        private async Task RunOnceSafelyAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Program.ProcessOnceAsync(_services, _dryRun, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while processing allocations");
            }
        }
    }
}
