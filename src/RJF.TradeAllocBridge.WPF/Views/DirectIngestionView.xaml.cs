using System.IO;
using System.Windows;
using System.Windows.Controls;
using RJF.TradeAllocBridge.WPF;
using Microsoft.Extensions.DependencyInjection;
using RJF.TradeAllocBridge.WPF.ViewModels;

namespace RJF.TradeAllocBridge.WPF.Views
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

        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }
    }
}
