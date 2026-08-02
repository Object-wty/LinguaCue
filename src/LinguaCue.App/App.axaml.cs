using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LinguaCue.Infrastructure;
using LinguaCue.Services;
using LinguaCue.ViewModels;
using LinguaCue.Views;

namespace LinguaCue;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var layout = PortableLayout.Create();
            var toolResolver = new RuntimeToolResolver(layout);
            var subtitleService = new SrtSubtitleService();
            var workerClient = new SubtitleWorkerClient(layout);
            var settingsService = new UserSettingsService(layout);
            var scheduler = new TaskQueueScheduler(settingsService.Load().MaxConcurrentTasks);

            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(
                new DesktopStorageService(window),
                new AvaloniaThemeService(this),
                new ModelImportService(layout),
                toolResolver,
                subtitleService,
                workerClient,
                scheduler,
                settingsService);
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            viewModel.Initialize();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
