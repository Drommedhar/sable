using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

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
            // resolves during InitializeComponent. Locale JSON ships beside the exe (csproj Content)
            // on Windows/Linux, but inside a macOS .app it lives in Contents/Resources — AppPaths
            // resolves whichever applies.
            var settings = Sable.Core.Settings.SettingsService.Load();
            var localesDir = Sable.Core.AppPaths.ResolveContent(System.IO.Path.Combine("Assets", "Locales"));
            Sable.App.Localization.Loc.Instance.Initialize(localesDir, settings.Language);

            // closing the main window quits the app, regardless of open tool windows
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // splash first. MainWindow's ctor is HEAVY (GPU init, settings, session restore) and runs
            // on this same UI thread — built during the intro it stalls the animation, so it is
            // deferred until the intro has fully played, then the splash cross-fades into it.
            var splash = new SplashWindow();
            splash.Show();
            // async void: a MainWindow ctor exception rethrows on the dispatcher and crashes
            // loudly — a discarded task swallowed it silently behind the splash before.
            Dispatcher.UIThread.Post(async void () =>
            {
                try { await ShowMainAsync(desktop, splash); }
                catch (System.Exception ex)
                {
                    System.Console.Error.WriteLine(ex);
                    throw;
                }
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowMainAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        // let the intro play on an idle UI thread; the splash holds its final frame while the
        // main window builds afterwards
        await splash.IntroDone;

        var main = new MainWindow(desktop.Args) { Opacity = 0 };
        desktop.MainWindow = main;
        try
        {
            var opened = new TaskCompletionSource();
            main.Opened += (_, _) => opened.TrySetResult();
            main.Show();
            await opened.Task;

            // cross-fade: splash out while the main window fades in
            var fadeOut = splash.FadeOutAsync();
            await SplashWindow.FadeAsync(main, 0, 1, 400);
            await fadeOut;
        }
        catch
        {
            // never leave the app invisible behind a stuck splash
            main.Opacity = 1;
            if (splash.IsVisible)
                splash.Close();
            throw;
        }
    }
}
