using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vortnux.Desktop;

[assembly: AvaloniaTestApplication(typeof(Vortnux.Tests.TestAppBuilder))]

namespace Vortnux.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{
    [AvaloniaFact]
    public void SidebarButtonsOpenEveryRouteAndWindowRenders()
    {
        using var fixture = new Fixture(); var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            foreach (var route in new[] { "Games", "Browse", "Mods", "Collections", "Downloads", "Activity", "Settings" })
            {
                var button = window.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, route));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(route, window.CurrentPage);
            }
            window.Navigate("Mods");
            var target = Environment.GetEnvironmentVariable("VORTNUX_SCREENSHOT") ?? Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots/library.png");
            if (target is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Save(target, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ActivityActivationSelectsExactGameAndModRepeatedly()
    {
        using var fixture = new Fixture(); var mod = fixture.Stage(); var activity = fixture.Manager.Install(mod);
        var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            window.Navigate("Settings"); window.Activate(activity);
            Assert.Equal("Mods", window.CurrentPage); Assert.Equal(mod.Id, window.FocusedEntity); Assert.Equal(fixture.Game.Id, window.CurrentGameId);
            Assert.True(Assert.Single(fixture.Manager.State.Activities).IsRead);
            window.Navigate("Games"); window.Activate(activity); Assert.Equal(mod.Id, window.FocusedEntity);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CollectionActivityFocusesCollectionAndRemovedModFallsBackToHistory()
    {
        using var fixture = new Fixture(); var member = fixture.Stage(collection: "fixture@1");
        var installed = fixture.Manager.InstallBatch([member], fixture.Game.Id, "Collection installed", "Fixture", "fixture@1");
        var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            window.Activate(installed); Assert.Equal("Collections", window.CurrentPage); Assert.Equal("fixture@1", window.FocusedEntity);
            fixture.Manager.Remove(member.Id); window.Activate(installed);
            Assert.Equal("Activity", window.CurrentPage); Assert.Equal(installed.Id, window.FocusedEntity);
        }
        finally { window.Close(); }
    }
}
