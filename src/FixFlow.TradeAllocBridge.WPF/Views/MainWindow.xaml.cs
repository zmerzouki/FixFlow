using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FixFlow.TradeAllocBridge.WPF.ViewModels;

namespace FixFlow.TradeAllocBridge.WPF.Views
{
    public partial class MainWindow : Window
    {
        private readonly MapEditorViewModel _mapEditorViewModel;

        public MainWindow(AllocationProcessorViewModel viewModel, MapEditorViewModel mapEditorViewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _mapEditorViewModel = mapEditorViewModel;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_mapEditorViewModel.IsEditing)
            {
                var result = ShowUnsavedChangesPrompt();

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == MessageBoxResult.Yes)
                {
                    if (_mapEditorViewModel.SaveMappingCommand?.CanExecute(null) == true)
                    {
                        _mapEditorViewModel.SaveMappingCommand.Execute(null);
                    }
                    else
                    {
                        MessageBox.Show(
                            this,
                            "Cannot save the mapping yet. Please complete required fields or cancel to continue editing.",
                            "Save Incomplete",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        e.Cancel = true;
                        return;
                    }
                }
            }

            base.OnClosing(e);
        }

        private MessageBoxResult ShowUnsavedChangesPrompt()
        {
            MessageBoxResult result = MessageBoxResult.Cancel;

            // Build content with button references so we can wire clicks directly
            var grid = new Grid
            {
                Margin = new Thickness(16)
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock
            {
                Text = "Want to save your changes?",
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(text, 0);
            grid.Children.Add(text);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var yesButton = new Button { Content = "Yes", MinWidth = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var noButton = new Button { Content = "No", MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
            var cancelButton = new Button { Content = "Cancel", MinWidth = 70 };

            buttonPanel.Children.Add(yesButton);
            buttonPanel.Children.Add(noButton);
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            var dialog = new Window
            {
                Title = "Unsaved Changes",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = grid
            };

            void OnClose(MessageBoxResult r)
            {
                result = r;
                dialog.DialogResult = true;
            }

            yesButton.Click += (_, __) => OnClose(MessageBoxResult.Yes);
            noButton.Click += (_, __) => OnClose(MessageBoxResult.No);
            cancelButton.Click += (_, __) => OnClose(MessageBoxResult.Cancel);

            dialog.ShowDialog();
            return result;
        }
    }
}
