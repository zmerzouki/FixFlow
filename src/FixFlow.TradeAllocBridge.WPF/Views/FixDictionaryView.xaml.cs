using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FixFlow.TradeAllocBridge.WPF.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FixFlow.TradeAllocBridge.WPF.Views
{
    public partial class FixDictionaryView : UserControl
    {
        public FixDictionaryView() : this(((App)Application.Current).Services.GetRequiredService<FixDictionaryViewModel>())
        {
        }

        public FixDictionaryView(FixDictionaryViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void MessageSearchTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandleSuggestionKeyDown(
                e,
                MessageSuggestionsListBox,
                commit: () => { if (DataContext is FixDictionaryViewModel vm) vm.CommitSelectedMessageSuggestion(); });
        }

        private void TagSearchTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandleSuggestionKeyDown(
                e,
                TagSuggestionsListBox,
                commit: () => { if (DataContext is FixDictionaryViewModel vm) vm.CommitSelectedTagSuggestion(); });
        }

        private void MessageSuggestionsListBox_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is FixDictionaryViewModel vm)
            {
                vm.CommitSelectedMessageSuggestion();
            }
        }

        private void TagSuggestionsListBox_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is FixDictionaryViewModel vm)
            {
                vm.CommitSelectedTagSuggestion();
            }
        }

        private void HandleSuggestionKeyDown(KeyEventArgs e, ListBox listBox, Action commit)
        {
            if (listBox.Items.Count == 0)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                if (listBox.SelectedItem != null)
                {
                    commit();
                    e.Handled = true;
                }

                return;
            }

            if (e.Key != Key.Down && e.Key != Key.Up)
            {
                return;
            }

            var vm = DataContext as FixDictionaryViewModel;
            vm?.SetSuggestionCommitSuppressed(true);

            try
            {
                var index = listBox.SelectedIndex;
                if (index < 0)
                {
                    index = e.Key == Key.Down ? 0 : listBox.Items.Count - 1;
                }
                else if (e.Key == Key.Down)
                {
                    index = Math.Min(index + 1, listBox.Items.Count - 1);
                }
                else if (e.Key == Key.Up)
                {
                    index = Math.Max(index - 1, 0);
                }

                listBox.SelectedIndex = index;
                listBox.ScrollIntoView(listBox.SelectedItem);
                e.Handled = true;
            }
            finally
            {
                vm?.SetSuggestionCommitSuppressed(false);
            }
        }
    }
}
