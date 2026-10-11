using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vortnux.Desktop;

namespace Vortnux.Tests;

public sealed class LaunchTests
{
    [AvaloniaFact]
    public async Task SecondaryLaunchDeliversActivationAndLinksAndOwnershipCanRestart()
    {
        using var fixture = new Fixture(); var directory = fixture.Manager.Store.DirectoryPath;
        using var owner = LaunchCoordinator.TryOwn(directory);
        Assert.NotNull(owner); Assert.Null(LaunchCoordinator.TryOwn(directory));
        var received = new List<string?>();
        Assert.True(await LaunchCoordinator.ForwardAsync(directory, null)); // Received before UI readiness.
        var link = "nxm://cyberpunk2077/mods/1/files/2?key=encoded%2Bkey&expires=9999999999";
        Assert.True(await LaunchCoordinator.ForwardAsync(directory, link));
        owner.Attach(link => { received.Add(link); return true; }); Dispatcher.UIThread.RunJobs();
        Assert.Equal(new string?[] { null, link }, received);
        owner.Accepting = false;
        Assert.False(await LaunchCoordinator.ForwardAsync(directory, null));
        owner.Dispose();
        using var restarted = LaunchCoordinator.TryOwn(directory);
        Assert.NotNull(restarted);
    }

    [AvaloniaFact]
    public void ReopeningPreservesPageAndLinkRestoresMinimizedWindow()
    {
        using var fixture = new Fixture(); var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            window.Navigate("Mods"); window.ReceiveLaunch(null);
            Assert.Equal("Mods", window.CurrentPage);
            window.WindowState = WindowState.Minimized;
            window.ReceiveLaunch("nxm://cyberpunk2077/mods/1/files/2");
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal("Downloads", window.CurrentPage);
            Assert.Equal(fixture.Game.Id, window.CurrentGameId);
            window.ReceiveLaunch("nxm://othergame/mods/1/files/2");
            Assert.Equal(OperationStatus.Failed, Assert.Single(window.Operations.Entries).Status);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NotificationBellShowsProgressCancelsAndMarksHistoryRead()
    {
        using var fixture = new Fixture(); var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            var cancelled = false;
            var active = window.Operations.Begin("Downloading RED4ext", () => cancelled = true);
            window.Operations.Report(active, 42, "42% downloaded");
            window.Operations.Finish(window.Operations.Begin("Mod installed", () => { }, "Mods"), OperationStatus.Completed, "Installation verified");
            Assert.Equal(1, window.Operations.UnreadCount);
            var bell = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "NotificationBell");
            var flyout = Assert.IsType<Flyout>(bell.Flyout); flyout.ShowAt(bell);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, window.Operations.UnreadCount);
            var content = Assert.IsType<ScrollViewer>(flyout.Content);
            Assert.Equal(42, Assert.Single(content.GetVisualDescendants().OfType<ProgressBar>()).Value);
            var cancel = Assert.Single(content.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Cancel"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(cancelled);
            if (Environment.GetEnvironmentVariable("VORTNUX_NOTIFICATIONS_SCREENSHOT") is { } target)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(400, 480));
                bitmap.Render(content); bitmap.Save(target, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            flyout.Hide();
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ClearingNotificationsPreservesActiveOperationsAndHistoryIsBounded()
    {
        var center = new OperationCenter(); var cancelled = false;
        var active = center.Begin("Download", () => cancelled = true);
        center.Report(active, 42, "42% downloaded");
        Assert.Equal(42, active.Progress);
        for (var index = 0; index < 205; index++)
            center.Finish(center.Begin("Result", () => { }), OperationStatus.Completed, "Ready");
        Assert.Equal(201, center.Entries.Count); Assert.Equal(200, center.UnreadCount);
        center.MarkAllRead(); Assert.Equal(0, center.UnreadCount);
        center.ClearHistory(); Assert.Same(active, Assert.Single(center.Entries));
        center.Cancel(active); Assert.True(cancelled);
        center.Finish(active, OperationStatus.Cancelled, "Cancelled");
        center.Report(active, 100, "Late progress"); Assert.Equal(42, active.Progress);
    }
}
