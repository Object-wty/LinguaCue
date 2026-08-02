using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using LinguaCue.ViewModels;

namespace LinguaCue.Views;

public sealed partial class MainWindow : Window
{
    private bool closeConfirmed;
    private bool closeDialogOpen;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closeConfirmed || DataContext is not MainWindowViewModel { HasActiveTasks: true } viewModel)
        {
            return;
        }

        e.Cancel = true;
        if (closeDialogOpen)
        {
            return;
        }

        closeDialogOpen = true;
        try
        {
            if (!await ShowCancelTasksDialogAsync())
            {
                return;
            }

            viewModel.CancelAllTasks();
            closeConfirmed = true;
            Close();
        }
        finally
        {
            closeDialogOpen = false;
        }
    }

    private async Task<bool> ShowCancelTasksDialogAsync()
    {
        var dialog = new Window
        {
            Title = "LinguaCue",
            Width = 430,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "仍有任务正在处理",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "关闭 LinguaCue 将取消所有正在运行和排队的转换、烧录任务。要继续关闭吗？",
                    TextWrapping = TextWrapping.Wrap
                },
                CreateDialogButtons()
            }
        };

        return await dialog.ShowDialog<bool>(this);

        StackPanel CreateDialogButtons()
        {
            var keepButton = new Button { Content = "继续处理", MinWidth = 96 };
            var closeButton = new Button { Content = "取消任务并关闭", MinWidth = 126 };
            keepButton.Click += (_, _) => dialog.Close(false);
            closeButton.Click += (_, _) => dialog.Close(true);
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 9,
                Children = { keepButton, closeButton }
            };
        }
    }
}
