using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Sable.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // i18n: initialize the locale system BEFORE the main window loads, since {loc:Loc}
            // resolves during InitializeComponent. Locale JSON ships next to the exe (csproj Content).
            var settings = Sable.Core.Settings.SettingsService.Load();
            var localesDir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "Locales");
            Sable.App.Localization.Loc.Instance.Initialize(localesDir, settings.Language);

            // closing the main window quits the app, regardless of open tool windows
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(desktop.Args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}