using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Scan;

public sealed partial class App : Application
{
    private OmarchyTheme? theme;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        OmarchyTheme.Apply(ThemePalette.Default);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel(new ScanPreferencesStore());
            theme = new OmarchyTheme(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            theme.StatusChanged += message => viewModel.ThemeStatus = message;
            var window = new MainWindow(viewModel);
            desktop.MainWindow = window;
            theme.Start();
            desktop.Exit += (_, _) => theme.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
