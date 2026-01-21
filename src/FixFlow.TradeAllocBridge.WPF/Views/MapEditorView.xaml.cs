using System.Windows;
using System.Windows.Controls;
using FixFlow.TradeAllocBridge.WPF;
using Microsoft.Extensions.DependencyInjection;
using FixFlow.TradeAllocBridge.WPF.ViewModels;

namespace FixFlow.TradeAllocBridge.WPF.Views
{
    public partial class MapEditorView : UserControl
    {
        public MapEditorView() : this(((App)Application.Current).Services.GetRequiredService<MapEditorViewModel>())
        {
        }

        public MapEditorView(MapEditorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // Refresh mappings when tab becomes visible to recover from state after Direct Ingestion
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible && DataContext is MapEditorViewModel vm)
                {
                    vm.LoadAvailableMappings();
                    vm.RefreshSelectedMapping();
                }
            };
        }
    }
}
