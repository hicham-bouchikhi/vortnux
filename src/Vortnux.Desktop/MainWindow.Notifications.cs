using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vortnux.Core;

namespace Vortnux.Desktop;

public sealed partial class MainWindow
{
    public OperationCenter Operations { get; } = new();
    private readonly Queue<(string Link, OperationEntry Entry)> incomingLinks = new();
    private readonly Button notificationBell = new() { Name = "NotificationBell", Padding = new Thickness(12, 8) };
    private readonly StackPanel notificationItems = new() { Spacing = 12, Width = 360 };
    private OperationEntry? currentOperation;
    private bool backgroundOperation;

    private void InitializeNotifications()
    {
        var flyout = new Flyout { Content = new ScrollViewer { Content = notificationItems, MaxHeight = 480, Background = Brush("Card"), Padding = new Thickness(12) } };
        notificationBell.Flyout = flyout;
        flyout.Opened += (_, _) => Operations.MarkAllRead();
        Operations.Changed += RenderNotifications;
        RenderNotifications();
    }

    private void RenderNotifications()
    {
        if (closed) return;
        var bellContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bellContent.Children.Add(new PathIcon
        {
            Width = 18, Height = 18, Foreground = Brush(Operations.ActiveCount > 0 ? "Accent" : "Text"),
            Data = Geometry.Parse("M21,19V20H3V19L5,17V11C5,7.9 7.03,5.17 10,4.29C10,4.19 10,4.1 10,4A2,2 0 0,1 12,2A2,2 0 0,1 14,4C14,4.1 14,4.19 14,4.29C16.97,5.17 19,7.9 19,11V17L21,19M14,21A2,2 0 0,1 12,23A2,2 0 0,1 10,21")
        });
        if (Operations.ActiveCount > 0) bellContent.Children.Add(Text($"{Operations.ActiveCount} active", 12));
        if (Operations.UnreadCount > 0)
            bellContent.Children.Add(new Border { Background = Brush("Selected"), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1),
                Child = Text(Operations.UnreadCount > 9 ? "9+" : Operations.UnreadCount.ToString(), 12) });
        notificationBell.Content = bellContent;
        ToolTip.SetTip(notificationBell, "Notifications and downloads");
        notificationItems.Children.Clear();
        notificationItems.Children.Add(Row(Text("Notifications", 19), Button("Clear history", Operations.ClearHistory)));
        if (Operations.Entries.Count == 0) notificationItems.Children.Add(Text("No notifications yet", 13));
        foreach (var entry in Operations.Entries.OrderBy(entry => entry.Status is OperationStatus.Running or OperationStatus.Queued ? 0 : 1))
        {
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(Text(entry.Title, 15));
            content.Children.Add(Text($"{entry.Timestamp.LocalDateTime:HH:mm} · {entry.Status} · {entry.Message}", 12, "Muted"));
            if (entry.Status == OperationStatus.Running)
                content.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = entry.Progress, Height = 6 });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (entry.Cancel is not null) actions.Children.Add(Button("Cancel", () => Operations.Cancel(entry)));
            if (entry.Page is not null || entry.Open is not null)
            {
                var open = Button(entry.Status == OperationStatus.Queued ? "Open link" : "Open", () =>
                {
                    entry.IsRead = true;
                    if (entry.Open is { } action) action(); else Navigate(entry.Page!);
                    Operations.Refresh();
                });
                open.IsEnabled = entry.Status != OperationStatus.Queued || !busy && staged.Count == 0;
                actions.Children.Add(open);
            }
            content.Children.Add(actions); notificationItems.Children.Add(content);
        }
    }

    public bool ReceiveLaunch(string? link)
    {
        if (closed) return false;
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
        if (link is null) return true;
        object parsed;
        try { parsed = NexusLinks.Parse(link); }
        catch (ModManagerException)
        {
            var invalid = Operations.Begin("Invalid Nexus link", () => { });
            Operations.Finish(invalid, OperationStatus.Failed, "This link is not supported."); return true;
        }
        if (busy || staged.Count > 0 || !string.IsNullOrWhiteSpace(linkText) || resolvedMod is not null || collection is not null || incomingLinks.Count > 0)
        {
            var title = parsed switch
            {
                NexusModLink mod => $"Nexus mod {mod.ModId}" + (mod.FileId is { } file ? $" · file {file}" : ""),
                NexusCollectionLink collectionLink => "Nexus collection " + collectionLink.Slug,
                _ => "Nexus link received"
            };
            var entry = Operations.Begin(title, () => { });
            entry.Status = OperationStatus.Queued; entry.Message = "Waiting for the current download or review";
            entry.Cancel = () => { entry.Open = null; Operations.Finish(entry, OperationStatus.Cancelled, "Link dismissed"); DrainIncomingLinks(); };
            entry.Open = () => OpenIncomingLink(link, entry);
            incomingLinks.Enqueue((link, entry)); Operations.Refresh(); return true;
        }
        OpenIncomingLink(link);
        return true;
    }

    private void OpenIncomingLink(string link, OperationEntry? entry = null)
    {
        if (busy || staged.Count > 0) return;
        if (entry is not null)
        {
            if (entry.Status != OperationStatus.Queued) return;
            Operations.Finish(entry, OperationStatus.Completed, "Opened in Downloads"); entry.Open = null;
        }
        selectedCatalogItem = null; installationRequested = false;
        resolvedMod = null; resolvedFiles = []; collection = null; collectionPlan = null;
        linkText = link; Navigate("Downloads");
        if (nexus is not null) _ = Resolve();
    }

    private void DrainIncomingLinks()
    {
        while (incomingLinks.TryPeek(out var next) && next.Entry.Status != OperationStatus.Queued) incomingLinks.Dequeue();
        if (busy || staged.Count > 0 || !string.IsNullOrWhiteSpace(linkText) || resolvedMod is not null || collection is not null) return;
        if (incomingLinks.TryDequeue(out var incoming)) OpenIncomingLink(incoming.Link, incoming.Entry);
    }
}
