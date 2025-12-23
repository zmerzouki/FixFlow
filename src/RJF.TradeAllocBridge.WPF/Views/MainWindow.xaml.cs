using System.Windows;
using RJF.TradeAllocBridge.WPF.ViewModels;

namespace RJF.TradeAllocBridge.WPF.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow(AllocationProcessorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
