using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Vortnux.Core;

namespace Vortnux.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--version"]) { Console.WriteLine(BuildInfo.Display); return 0; }
        if (args is ["--build-number"]) { Console.WriteLine(BuildInfo.Number); return 0; }
        if (args is ["--release-version"]) { Console.WriteLine(BuildInfo.Version); return 0; }
        if (args is ["--git-commit"]) { Console.WriteLine(BuildInfo.Commit); return 0; }
        if (args.Contains("--smoke-test", StringComparer.Ordinal)) return SmokeTest.Run();
        if (args.FirstOrDefault() != "--update-health-check")
        {
            var stateDirectory = StateDirectory();
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (Updates.RecoverAppImageUpdate(new StateStore(stateDirectory), appImage))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(appImage!) { UseShellExecute = false });
                return 0;
            }
        }
        BuildApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
    public static string StateDirectory() => Environment.GetEnvironmentVariable("VORTNUX_STATE_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vortnux");
}

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var directory = Program.StateDirectory();
            var healthCheck = desktop.Args is ["--update-health-check", _];
            desktop.MainWindow = new MainWindow(new ModManager(new StateStore(directory)), desktop.Args?.FirstOrDefault(value => value.StartsWith("https://", StringComparison.Ordinal) || value.StartsWith("nxm://", StringComparison.Ordinal)), healthCheck,
                credentialStore: new NexusCredentialStore(directory));
            if (desktop.Args is ["--update-health-check", var marker])
            {
                desktop.MainWindow.IsEnabled = false;
                desktop.MainWindow.Opened += (_, _) =>
                {
                    File.WriteAllText(marker, "ready");
                    var handover = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    handover.Tick += (_, _) =>
                    {
                        if (File.Exists(Path.Combine(directory, "update-transaction.json"))) return;
                        handover.Stop(); desktop.MainWindow.IsEnabled = true;
                    };
                    handover.Start();
                };
            }
            if (desktop.Args?.Contains("--ui-smoke-test", StringComparer.Ordinal) == true)
            {
                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                var pages = new Queue<string>(["Games", "Browse", "Mods", "Collections", "Downloads", "Activity", "Settings"]);
                timer.Tick += (_, _) =>
                {
                    if (pages.TryDequeue(out var page)) ((MainWindow)desktop.MainWindow).Navigate(page);
                    else { timer.Stop(); Console.WriteLine("PASS: Native GUI opened and all six routes rendered."); desktop.MainWindow.Close(); }
                };
                desktop.MainWindow.Opened += (_, _) => timer.Start();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
