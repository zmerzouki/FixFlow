using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Email;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;
using FixFlow.TradeAllocBridge.Core.Logging;
using FixFlow.TradeAllocBridge.Core.Mapping;
using FixFlow.TradeAllocBridge.Core.Reporting;
using FixFlow.TradeAllocBridge.WPF.ViewModels;
using FixFlow.TradeAllocBridge.WPF.Views;

namespace FixFlow.TradeAllocBridge.WPF
{
    public partial class App : Application
    {
        private IServiceProvider? _serviceProvider;

        public IServiceProvider Services =>
            _serviceProvider ?? throw new InvalidOperationException("Service provider not initialized yet.");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                var services = new ServiceCollection();
                ConfigureServices(services);
                _serviceProvider = services.BuildServiceProvider();

                var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize application: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        private void ConfigureServices(ServiceCollection services)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();

            var appConfig = configuration.Get<AppConfig>() ?? new AppConfig(); // This now works
            LogService.Configure(appConfig.Logging);

            // Register core services
            services.AddSingleton(configuration);
            services.AddSingleton(appConfig);
            services.AddSingleton(appConfig.Email);
            services.AddSingleton(appConfig.Fix);
            services.AddSingleton(appConfig.Logging);

            services.AddSingleton<ExcelParser>();
            services.AddSingleton<FixApp>();
            services.AddSingleton<FixEngine>();
            services.AddSingleton<FixClient>();
            services.AddSingleton<ValidationReport>();
            services.AddSingleton(provider =>
            {
                var configsPath = Path.Combine(AppContext.BaseDirectory, "configs");
                return new FixMappingRepository(configsPath);
            });

            // Register logging
            services.AddLogging(builder =>
                builder.AddSerilog());

            // Register ViewModels and Views
            services.AddTransient<AllocationProcessorViewModel>();
            services.AddSingleton<MainWindow>();

            // Add these registrations in ConfigureServices:
            services.AddSingleton<MapEditorViewModel>();
            services.AddTransient<MapEditorView>();
            services.AddTransient<DirectIngestionView>();
            services.AddTransient<MessageHistoryView>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            Log.CloseAndFlush();
        }
    }
}
