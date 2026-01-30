using System.Windows;
using System.Windows.Controls;
using FixFlow.TradeAllocBridge.WPF.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FixFlow.TradeAllocBridge.WPF.Views
{
    public partial class MessageLogView : UserControl
    {
        public MessageLogView() : this(((App)Application.Current).Services.GetRequiredService<MessageLogViewModel>())
        {
        }

        public MessageLogView(MessageLogViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void CopySelected_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MessageLogViewModel vm)
            {
                vm.CopyEntries(MessageLogGrid.SelectedItems);
            }
        }

        private void CopyRawFix_Click(object sender, RoutedEventArgs e)
        {
            if (MessageLogGrid.CurrentItem is MessageLogEntry entry && !string.IsNullOrWhiteSpace(entry.RawFixMessage))
            {
                Clipboard.SetText(entry.RawFixMessage);
            }
        }
    }
}
