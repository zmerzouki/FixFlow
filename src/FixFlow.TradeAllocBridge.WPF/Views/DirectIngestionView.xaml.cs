using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using FixFlow.TradeAllocBridge.WPF;
using Microsoft.Extensions.DependencyInjection;
using FixFlow.TradeAllocBridge.WPF.ViewModels;

namespace FixFlow.TradeAllocBridge.WPF.Views
{
    public partial class DirectIngestionView : UserControl
    {
        public DirectIngestionView() : this(((App)Application.Current).Services.GetRequiredService<AllocationProcessorViewModel>())
        {
        }

        public DirectIngestionView(AllocationProcessorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // Setup drag-drop
            DropZone.Drop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files.Length > 0)
                    {
                        var file = files[0];
                        var ext = Path.GetExtension(file).ToLower();
                        if (ext is ".xlsx" or ".xls" or ".csv")
                        {
                            viewModel.SelectFile(file);
                        }
                        else
                        {
                            MessageBox.Show("Unsupported file type. Please use .xlsx, .xls, or .csv files.", "Invalid File", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
            };

            DropZone.DragOver += (s, e) =>
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };
        }
        private void CopyResultMessage_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is AllocationProcessorViewModel vm && !string.IsNullOrWhiteSpace(vm.ResultMessage))
            {
                Clipboard.SetText(vm.ResultMessage);    
            }
        }

        // Added to satisfy ListBox SelectionChanged event declared in XAML.
        // Updates the view model's SelectedClient based on the ListBox selection.
        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not AllocationProcessorViewModel vm) return;

            if (sender is ListBox lb)
            {
                if (lb.SelectedItem is KeyValuePair<string, string> kv)
                {
                    vm.SelectedClient = kv;
                }
                else
                {
                    vm.SelectedClient = null;
                }
            }
        }
    }
}
