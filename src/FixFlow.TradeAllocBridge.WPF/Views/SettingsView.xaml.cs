using System.Windows;
using System.Windows.Controls;
using FixFlow.TradeAllocBridge.WPF.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FixFlow.TradeAllocBridge.WPF.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView() : this(((App)Application.Current).Services.GetRequiredService<SettingsViewModel>())
        {
        }

        public SettingsView(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            IsVisibleChanged += (_, __) =>
            {
                if (IsVisible && DataContext is SettingsViewModel vm)
                {
                    vm.LoadSettings();
                }
            };
        }
    }
}
