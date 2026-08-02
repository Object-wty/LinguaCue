using Avalonia;
using Avalonia.Styling;

namespace LinguaCue.Services;

public interface IThemeService
{
    void SetDarkMode(bool enabled);
}

public sealed class AvaloniaThemeService(Application application) : IThemeService
{
    public void SetDarkMode(bool enabled) =>
        application.RequestedThemeVariant = enabled ? ThemeVariant.Dark : ThemeVariant.Light;
}

