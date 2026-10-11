using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Vortnux.Core;
using Activity = Vortnux.Core.Activity;

namespace Vortnux.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ModManager manager;
    private readonly ReleaseClient releases = new();
    private UpdateManifest? availableUpdate;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly CatalogImages catalogImages;
    private readonly INexusCredentialStore? credentials;
    private readonly CancellationTokenSource credentialLifetime = new();
    private readonly StackPanel body = new() { Spacing = 16, Margin = new Thickness(32) };
    private readonly ScrollViewer scroll;
    private readonly TextBlock status;
    private readonly TextBlock heading;
    private readonly ComboBox games = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel navigation = new() { Spacing = 8 };
    private readonly Dictionary<string, Button> routes = [];
    private WindowNotificationManager? notifications;
    private string page = "Mods";
    private string? gameId;
    private string? focused;
    private bool busy;
    private bool refreshing;
    private bool quiet;
    private bool closed;
    private CancellationTokenSource? cancellation;
    private NexusApi? nexus;
    private NexusAccount? account;
    private string linkText = "";
    private readonly List<ManagedMod> staged = [];
    private NexusModLink? resolvedMod;
    private List<CollectionFile> resolvedFiles = [];
    private NexusCollection? collection;
    private CollectionPlan? collectionPlan;
    private string? batchId;
    private bool includeOptional;
    private bool replaceConflicts;
    private Activity? pendingActivity;
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 6 };

    public string CurrentPage => page;
    public string? FocusedEntity => focused;
    public string? CurrentGameId => gameId;

    public MainWindow(ModManager manager, string? startupLink = null, bool updateReadiness = false, HttpClient? httpClient = null, INexusCredentialStore? credentialStore = null)
    {
        this.manager = manager;
        appearance = AppearanceSettings.Load(manager.Store.DirectoryPath);
        InitializeAppearance();
        status = Text("Ready · all changes are verified", 13);
        heading = Text("Your mod library", 30);
        credentials = credentialStore;
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        catalogImages = new CatalogImages(http);
        Title = "Manage mods from Nexus Mods";
        Width = 1120; Height = 760; MinWidth = 820; MinHeight = 580;
        Background = Brush("Canvas");
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        var side = new StackPanel { Margin = new Thickness(20, 28), Spacing = 24 };
        side.Children.Add(Text("V O R T N U X", 23, "Accent"));
        side.Children.Add(new Border { Background = Brush("Selected"), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 7), Child = Text("MAKE IT YOUR GAME", 10, "Accent") });
        foreach (var route in new[] { "Games", "Browse", "Mods", "Collections", "Downloads", "Activity", "Settings" })
        {
            var button = Button(route, () => Navigate(route));
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            routes.Add(route, button); navigation.Children.Add(button);
        }
        side.Children.Add(navigation);
        side.Children.Add(Text("CYBERPUNK 2077\nProof of concept · 0.1.0", 12, "Muted"));
        var sidebar = new Border { Background = Brush("Sidebar"), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(3, 0, 0, 0), Child = side };
        shell.Children.Add(sidebar);
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetColumn(right, 1); shell.Children.Add(right);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(32, 26, 32, 8) };
        header.Children.Add(heading); Grid.SetColumn(games, 1); header.Children.Add(games);
        games.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            gameId = (games.SelectedItem as GameChoice)?.Id; focused = null;
            if (staged.Any(mod => mod.GameId != gameId)) { staged.Clear(); status.Text = "Staging cleared after switching game installation"; }
            Render();
        };
        right.Children.Add(header);
        scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); right.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(32, 12), Spacing = 8 };
        footer.Children.Add(progress); footer.Children.Add(Row(status, Button("Cancel download", () => cancellation?.Cancel())));
        Grid.SetRow(footer, 2); right.Children.Add(footer);
        Content = shell;
        Opened += (_, _) =>
        {
            notifications = new WindowNotificationManager(this) { MaxItems = 3, Position = NotificationPosition.BottomRight };
            if (startupLink is not null) { linkText = startupLink; Navigate("Downloads"); }
            if (credentials is not null && !updateReadiness) _ = RestoreAccountAsync(startupLink is null);
        };
        Closed += (_, _) => { closed = true; credentialLifetime.Cancel(); ClearThumbnailViews(); catalogImages.Dispose(); catalogSession?.Dispose(); cancellation?.Cancel(); if (ownsHttp) http.Dispose(); releases.Dispose(); };
        if (!updateReadiness)
        {
            try { manager.Recover(); }
            catch (Exception error) { status.Text = "Recovery required: " + error.Message; }
        }
        RefreshGames(); Render();
    }

    private sealed record GameChoice(string Id, string Label) { public override string ToString() => Label; }
    private IBrush Brush(string token) => themeBrushes[token];
    private TextBlock Text(string value, double size = 14, string color = "Text")
        => new() { Text = value, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(6) };
        button.Click += (_, _) => action();
        return button;
    }
    private static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }
    private Border Card(string title, string detail, params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Text(title, 19)); panel.Children.Add(Text(detail, 13, "Muted"));
        foreach (var control in controls) panel.Children.Add(control);
        return new Border { Background = Brush("Card"), BorderBrush = Brush("Outline"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Child = panel };
    }
    private string RequireGame() => gameId ?? throw new ModManagerException("Add and select a Cyberpunk installation in Games first.");

    public void Navigate(string route)
    {
        if (!routes.ContainsKey(route)) throw new ArgumentException("Unknown page.", nameof(route));
        page = route; focused = null; Render();
        if (page == "Browse" && catalogSession is { Result: null, Loading: false, Error: null }) _ = SearchCatalog();
    }

    public void Activate(Activity activity)
    {
        if (busy) { pendingActivity = activity; status.Text = "Item will open after the current operation"; return; }
        manager.MarkRead(activity.Id);
        gameId = activity.Target.GameId;
        page = routes.ContainsKey(activity.Target.Page) ? activity.Target.Page : "Activity";
        if (page == "Mods" && !manager.State.Mods.Any(mod => mod.GameId == gameId && mod.Id == activity.Target.EntityId) ||
            page == "Collections" && !manager.State.Mods.Any(mod => mod.GameId == gameId && mod.CollectionId == activity.Target.EntityId))
            page = "Activity";
        focused = page == "Activity" ? activity.Id : activity.Target.EntityId;
        modFilter = ""; modStateFilter = 0; collectionFilter = "";
        RefreshGames(); Render();
        Show(); Activate();
    }

    private void RefreshGames()
    {
        refreshing = true;
        var items = manager.State.Games.Select(game => new GameChoice(game.Id, game.Name + " · " + Path.GetFileName(game.Root))).ToList();
        if (!items.Any(item => item.Id == gameId)) gameId = items.FirstOrDefault()?.Id;
        games.ItemsSource = items; games.SelectedItem = items.FirstOrDefault(item => item.Id == gameId);
        refreshing = false;
    }

    private void Render()
    {
        if (closed) return;
        body.Children.Clear();
        ClearThumbnailViews();
        heading.Text = page switch { "Browse" => "Manage mods from Nexus Mods", "Mods" => "Your mod library", "Collections" => "Your installed collections", "Games" => "Your games", "Downloads" => "Bring in something new", "Activity" => "Activity & notifications", _ => "Make yourself at home" };
        foreach (var route in routes)
        {
            route.Value.Background = Brush(route.Key == page ? "Selected" : "Sidebar");
            route.Value.Foreground = Brush(route.Key == page ? "Accent" : "Muted");
            route.Value.BorderBrush = Brush(route.Key == page ? "Accent" : "Sidebar");
            route.Value.BorderThickness = new Thickness(3, 0, 0, 0);
            route.Value.FontWeight = route.Key == page ? FontWeight.SemiBold : FontWeight.Normal;
        }
        navigation.IsEnabled = !busy; games.IsEnabled = !busy;
        switch (page)
        {
            case "Games": RenderGames(); break;
            case "Browse": RenderBrowse(); break;
            case "Mods": RenderMods(); break;
            case "Collections": RenderCollections(); break;
            case "Downloads": RenderDownloads(); break;
            case "Activity": RenderActivity(); break;
            case "Settings": RenderSettings(); break;
        }
        body.IsEnabled = !busy;
    }

    private void RenderGames()
    {
        body.Children.Add(Card("Start with Night City", "Select your Steam, GOG, or Proton game directory. Vortnux checks the executable and preserves overwritten files.",
            Button("Add Cyberpunk installation", () => _ = Work(async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Cyberpunk 2077", AllowMultiple = false });
                if (folders.Count == 0) return;
                var path = folders[0].TryGetLocalPath() ?? throw new ModManagerException("Choose a local directory.");
                var game = await Task.Run(() => manager.AddGame(path)); gameId = game.Id; RefreshGames();
            }))));
        foreach (var game in manager.State.Games)
            body.Children.Add(Card(game.Name, game.Root, Button("Open mod library", () => { gameId = game.Id; RefreshGames(); Navigate("Mods"); })));
    }

    private void RenderMods()
    {
        var mods = manager.State.Mods.Where(mod => mod.GameId == gameId).ToList();
        body.Children.Add(Card($"{mods.Count} mods · {mods.Count(mod => mod.Enabled)} enabled", "Mods installed through Vortnux appear here. Browse Nexus to find new mods.",
            Row(Button("Import archive", () => _ = Import()), Button("Browse Nexus mods", () => OpenBrowse(CatalogKind.Mods)))));
        var results = CardGrid("InstalledModGrid");
        var filter = new TextBox { Name = "InstalledModSearch", Text = modFilter, PlaceholderText = "Filter installed mods by name", MinWidth = 280 };
        var state = new ComboBox { Name = "InstalledModState", ItemsSource = new[] { "All", "Enabled", "Disabled" }, SelectedIndex = modStateFilter, MinWidth = 140 };
        filter.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty || closed || results.Parent != body) return;
            modFilter = filter.Text ?? ""; FillMods(results);
        };
        state.SelectionChanged += (_, _) => { modStateFilter = state.SelectedIndex; FillMods(results); };
        body.Children.Add(Row(filter, state)); body.Children.Add(results); FillMods(results);
    }

    private void FillMods(WrapPanel results)
    {
        results.Children.Clear();
        var mods = manager.State.Mods.Where(mod => mod.GameId == gameId && mod.Name.Contains(modFilter, StringComparison.OrdinalIgnoreCase) &&
            (modStateFilter == 0 || mod.Enabled == (modStateFilter == 1))).ToList();
        if (mods.Count == 0) results.Children.Add(Text("No installed mods match this filter.", 14, "Muted"));
        foreach (var mod in mods)
        {
            var card = Card(mod.Name, $"{(mod.Enabled ? "Enabled" : "Disabled")} · {mod.Files.Count} files" + (mod.CollectionId is null ? "" : " · collection member"),
                Text(string.Join("\n", mod.Files.Select(file => file.Path)), 12, "Muted"),
                CatalogActions(Button(mod.Enabled ? "Disable" : "Enable", () => _ = Change(() => manager.Toggle(mod.Id))),
                    Button("Uninstall", () => _ = ConfirmChange("Uninstall " + mod.Name + "?", "Tracked files will be removed or restored to the previous owner.", () => manager.Remove(mod.Id)))));
            card.Width = 360; AddEntity(card, mod.Id, results);
        }
    }

    private static WrapPanel CardGrid(string name) => new() { Name = name, Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 16 };

    private void AddEntity(Border card, string id, Panel? target = null)
    {
        (target ?? body).Children.Add(card);
        if (focused != id) return;
        card.BorderBrush = Brush("Accent"); card.BorderThickness = new Thickness(2);
        Dispatcher.UIThread.Post(() => card.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void RenderCollections()
    {
        body.Children.Add(Card("A collection is more than a list", "Supported: pinned Nexus files with plain Cyberpunk ZIP layouts. Installer choices, patches, external resources and ordering rules are checked before deployment.",
            Row(Button("Browse Nexus collections", () => OpenBrowse(CatalogKind.Collections)), Button("Import collection.json", () => _ = ImportManifest()))));
        var results = CardGrid("InstalledCollectionGrid");
        var filter = new TextBox { Name = "InstalledCollectionSearch", Text = collectionFilter, PlaceholderText = "Filter installed collections or member mods", MinWidth = 280 };
        filter.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty || closed || results.Parent != body) return;
            collectionFilter = filter.Text ?? ""; FillCollections(results);
        };
        body.Children.Add(filter); body.Children.Add(results); FillCollections(results);
    }

    private void FillCollections(WrapPanel results)
    {
        results.Children.Clear();
        var groups = manager.State.Mods.Where(mod => mod.GameId == gameId && mod.CollectionId is not null).GroupBy(mod => mod.CollectionId!)
            .Where(group => group.Key.Contains(collectionFilter, StringComparison.OrdinalIgnoreCase) || group.Any(mod => mod.Name.Contains(collectionFilter, StringComparison.OrdinalIgnoreCase))).ToList();
        if (groups.Count == 0) results.Children.Add(Text("No installed collections match this filter.", 14, "Muted"));
        foreach (var group in groups)
        {
            var id = group.Key;
            var card = Card(id, $"{group.Count()} managed mods\n" + string.Join("\n", group.Select(mod => mod.Name)),
                Button("Uninstall collection", () => _ = ConfirmChange("Uninstall collection?", "Only this collection's mods will be removed. Other owners and original files are restored.", () => manager.RemoveCollection(RequireGame(), id))));
            card.Width = 360; AddEntity(card, id, results);
        }
    }

    private void RenderDownloads()
    {
        if (selectedCatalogItem is { } selected)
            body.Children.Add(WithThumbnail(Card(selected.Name, selected.Summary + (installationRequested
                ? "\nChoose files to download, then review their destinations before installing." : ""),
                CatalogActions(Button("Back to results", () => Navigate("Browse")), Button("Open on Nexus", () => OpenWebsite(selected.Url)))), selected));
        var input = new TextBox { Text = linkText, PlaceholderText = "Nexus mod, collection, or nxm:// link", MinWidth = 400 };
        input.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) linkText = input.Text ?? ""; };
        body.Children.Add(Card("Nexus Mods", account is null ? "Connect an API key in Settings. Premium enables automatic file downloads; free accounts use authorized manager links." : $"Connected as {account.Name} · {(account.Premium ? "Premium" : "Free account")}",
            input, Row(Button("Resolve link", () => _ = Resolve()), Button("Import local archive", () => _ = Import()))));
        foreach (var file in resolvedFiles)
        {
            var chosen = file;
            body.Children.Add(Card(file.Name, $"Mod {file.ModId} · file {file.FileId}", account is { Premium: false } && resolvedMod?.Key is null
                ? Row(Button("Open file on Nexus", () => OpenWebsite($"https://www.nexusmods.com/cyberpunk2077/mods/{file.ModId}?tab=files&file_id={file.FileId}")), Text("Choose Download with manager, then paste its authorized link.", 12))
                : Button(installationRequested ? "Install this file" : "Download & stage", () => _ = DownloadMod(chosen))));
        }
        if (collection is not null && collectionPlan is null)
            body.Children.Add(Card(collection.Name, $"Revision {collection.Revision} · {collection.Files.Count} files", Button("Download & check manifest", () => _ = LoadRemoteManifest())));
        if (collectionPlan is not null)
        {
            var optional = new CheckBox { Content = "Include optional mods", IsChecked = includeOptional };
            optional.IsCheckedChanged += (_, _) => includeOptional = optional.IsChecked == true;
            var installCollection = Button(installationRequested ? "Install collection" : "Download & stage collection", () => _ = DownloadCollection());
            installCollection.IsEnabled = account is { Premium: true };
            body.Children.Add(Card(collectionPlan.Name, $"{collectionPlan.Entries.Count(entry => !entry.Optional)} required · {collectionPlan.Entries.Count(entry => entry.Optional)} optional\n" +
                string.Join("\n", collectionPlan.Entries.Select(entry => $"{entry.Name} · file {entry.FileId}{(entry.Optional ? " · optional" : "")}")),
                optional, installCollection, Text(account is { Premium: true } ? "Review staged destinations before deployment." : "Automatic collection downloads require a Nexus Premium account.", 12, "Muted")));
        }
        if (staged.Count > 0)
        {
            var conflicts = new CheckBox { Content = "Allow reviewed file overlaps (later staged mods win)", IsChecked = replaceConflicts };
            conflicts.IsCheckedChanged += (_, _) => replaceConflicts = conflicts.IsChecked == true;
            var occupied = manager.State.Mods.Where(mod => mod.GameId == gameId && mod.Enabled).SelectMany(mod => mod.Files).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var duplicate = staged.SelectMany(mod => mod.Files).GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preview = string.Join("\n", staged.SelectMany(mod => mod.Files.Select(file => $"{mod.Name} → {file.Path}{(occupied.Contains(file.Path) || duplicate.Contains(file.Path) ? "  [OVERLAP]" : "")}")));
            body.Children.Add(Card("Ready for review", $"{staged.Count} mods · {staged.Sum(mod => mod.Files.Count)} files\n" + preview,
                conflicts, Row(Button("Install reviewed files", () => _ = InstallStaged()), Button("Discard staging", () => { staged.Clear(); Render(); }))));
        }
    }

    private void RenderActivity()
    {
        body.Children.Add(Text("Click an event to open its game and exact mod or collection. Removed items open their activity record.", 14, "Muted"));
        foreach (var item in manager.State.Activities.AsEnumerable().Reverse())
        {
            var captured = item;
            AddEntity(Card((item.IsRead ? "" : "● ") + item.Title, item.Message + "\n" + item.Timestamp.ToLocalTime().ToString("g"),
                Button("Open item", () => Activate(captured))), item.Id);
        }
    }

    private void RenderSettings()
    {
        RenderAppearanceSettings();
        body.Children.Add(Card("About Vortnux", BuildInfo.Display));
        var key = new TextBox { Name = "NexusApiKey", PasswordChar = '●', PlaceholderText = "Personal Nexus API key", MinWidth = 350 };
        body.Children.Add(Card("Your Nexus account", (account is null ? "Not connected. " : $"Connected as {account.Name}. ") + (credentials?.Description ?? "Session-only connection.") + " Keys are sent only to api.nexusmods.com. Obtain your personal key from Nexus account settings.", key,
            Row(Button("Connect", () => _ = Work(async () =>
            {
                var value = key.Text ?? ""; key.Text = "";
                var api = new NexusApi(http, value); var user = await api.ValidateAsync(credentialLifetime.Token);
                if (credentials is not null) await credentials.SaveAsync(value, credentialLifetime.Token);
                SetAccount(api, user);
                resolvedMod = null; resolvedFiles = []; collection = null; collectionPlan = null; selectedCatalogItem = null; linkText = "";
                page = "Browse";
            })), Button("Retry saved connection", () => { if (credentials is not null) _ = RestoreAccountAsync(true); }), Button("Disconnect", () => { DisconnectAccount(); Render(); }), Button("Forget saved key", () => _ = Work(async () =>
            {
                if (credentials is not null) await credentials.ForgetAsync(credentialLifetime.Token);
                DisconnectAccount(); status.Text = "Saved API key removed";
            })))));
        body.Children.Add(Text("Disconnect ends this session. Forget saved key removes the stored key and disconnects.", 13, "Muted"));
        var quietBox = new CheckBox { Content = "Gaming mode · keep notifications inside Vortnux", IsChecked = quiet };
        quietBox.IsCheckedChanged += (_, _) => quiet = quietBox.IsChecked == true;
        body.Children.Add(Card("Notifications", "Every completed installation creates a persistent activity and clickable toast. Background desktop notifications include an Open action when the desktop supports it.", quietBox));
        body.Children.Add(Card("Recovery & backup", manager.Store.DirectoryPath,
            Row(Button("Recover interrupted deployment", () => _ = Work(() => Task.Run(manager.Recover))),
                Button("Backup database", () => _ = Work(async () =>
                {
                    var path = Path.Combine(manager.Store.DirectoryPath, "backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".sqlite3");
                    await Task.Run(() => manager.Store.Backup(path)); status.Text = "Backup saved: " + path;
                })))));
        body.Children.Add(Card("Release updates", releases.Trust is null ? "This development build has no trusted release publisher configured. Signed GitHub releases enable AppImage updates. Arch installations update through pacman." : "Publisher: " + releases.Trust.Repository + " · RSA signatures, SHA-256, native smoke test, database backup and GUI health check protect each AppImage update.",
            Button("Check GitHub release", () => _ = Work(async () =>
            {
                availableUpdate = await releases.CheckAsync(); status.Text = availableUpdate is null ? "You have the latest release" : "Verified release " + availableUpdate.Version;
            })), Text("Native AOT · .NET 10 · Avalonia 12\nOne managed configuration per installation in this proof of concept.", 13, "Muted")));
        if (availableUpdate is { } update)
            body.Children.Add(Card("Verified release " + update.Version, "The current AppImage will be kept as .previous. A database backup is made before replacement.",
                Button("Install update & restart", () => _ = InstallUpdate(update))));
    }

    private async Task Work(Func<Task> operation)
    {
        if (busy) return;
        busy = true; body.IsEnabled = false; navigation.IsEnabled = false; games.IsEnabled = false;
        status.Text = "Working…";
        try { await operation(); }
        catch (OperationCanceledException) { status.Text = "Cancelled · no pending files deployed"; }
        catch (Exception error)
        {
            status.Text = error.Message;
            if (manager.State.Journal is not null) status.Text += " · Recovery required in Settings";
            notifications?.Show(new Notification("Operation stopped", status.Text, NotificationType.Error));
        }
        finally
        {
            busy = false; cancellation?.Dispose(); cancellation = null; progress.Value = 0; RefreshGames(); Render();
            if (pendingActivity is { } pending) { pendingActivity = null; Activate(pending); }
            if (page == "Browse" && catalogSession is { Result: null, Loading: false, Error: null }) _ = SearchCatalog();
        }
    }

    private Task Change(Func<Activity> operation) => Work(async () => Notify(await Task.Run(operation)));
    private async Task InstallUpdate(UpdateManifest update)
    {
        if (!await Confirm("Install verified release " + update.Version + "?", "Vortnux will restart after the updated GUI passes its startup health check.")) return;
        await Work(async () =>
        {
            var current = Environment.GetEnvironmentVariable("APPIMAGE") ?? throw new ModManagerException("Launch the AppImage to update it. Arch installations update through pacman.");
            cancellation = new CancellationTokenSource();
            var download = Path.Combine(manager.Store.DirectoryPath, "update-" + Guid.NewGuid().ToString("N") + ".AppImage");
            try
            {
                await releases.DownloadAsync(update, download, cancellation.Token);
                await Updates.ApplyAppImageAsync(download, current, update, manager.Store, cancellation.Token);
                Close();
            }
            finally { if (File.Exists(download)) File.Delete(download); }
        });
    }
    private async Task ConfirmChange(string title, string detail, Func<Activity> operation)
    {
        if (busy) return;
        if (await Confirm(title, detail)) await Change(operation);
    }

    private async Task<bool> Confirm(string title, string detail)
    {
        var dialog = new Window { Title = title, Width = 480, Height = 230, CanResize = false, Background = Brush("Canvas"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Bind(RequestedThemeVariantProperty, this.GetObservable(ActualThemeVariantProperty));
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        panel.Children.Add(Text(title, 20)); panel.Children.Add(Text(detail));
        panel.Children.Add(Row(Button("Continue", () => dialog.Close(true)), Button("Cancel", () => dialog.Close(false))));
        dialog.Content = panel;
        return await dialog.ShowDialog<bool>(this);
    }

    private Task Import() => Work(async () =>
    {
        var id = RequireGame();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import Cyberpunk archive", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("ZIP / 7z archives") { Patterns = ["*.zip", "*.7z"] }] });
        var incoming = new List<ManagedMod>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath() ?? throw new ModManagerException("Choose a local archive.");
            incoming.Add(await Task.Run(() => manager.Stager.Stage(path, id)));
        }
        if (incoming.Count == 0) return;
        staged.Clear(); staged.AddRange(incoming); batchId = null; replaceConflicts = false; page = "Downloads";
        status.Text = "Staged · review destination paths before installation";
    });

    private Task InstallStaged() => Work(async () =>
    {
        var id = RequireGame();
        if (staged.Any(mod => mod.GameId != id)) throw new ModManagerException("Staged mods belong to another game installation. Import them again in the selected game.");
        var activity = await Task.Run(() => staged.Count == 1 && batchId is null ? manager.Install(staged[0], replaceConflicts) :
            manager.InstallBatch([.. staged], id, batchId is null ? "Mods installed" : "Collection installed",
                collectionPlan?.Name ?? "Imported mods", batchId ?? staged[0].Id, replaceConflicts));
        staged.Clear(); Notify(activity); status.Text = "Installed and verified";
    });

    private Task Resolve() => Work(async () =>
    {
        var api = nexus ?? throw new ModManagerException("Connect Nexus in Settings first.");
        var parsed = NexusLinks.Parse(linkText);
        if (selectedCatalogItem is { } selected && (parsed is NexusModLink selectedMod ? selected.ModId != selectedMod.ModId || selected.Kind != CatalogKind.Mods :
            parsed is NexusCollectionLink selectedCollection && (selected.Slug != selectedCollection.Slug || selected.Kind != CatalogKind.Collections))) selectedCatalogItem = null;
        resolvedFiles = []; resolvedMod = null; collection = null; collectionPlan = null;
        if (parsed is NexusModLink mod)
        {
            resolvedMod = mod;
            resolvedFiles = (await api.FilesAsync(mod)).Where(file => mod.FileId is null || mod.FileId == file.FileId).ToList();
            if (resolvedFiles.Count == 0) throw new ModManagerException("No available files matched this link.");
        }
        else if (parsed is NexusCollectionLink collectionLink) collection = await api.CollectionAsync(collectionLink);
        status.Text = installationRequested ? "Choose the file to install · downloads are reviewed before deployment" : "Resolved · choose files to stage";
    });

    private Task DownloadMod(CollectionFile file) => Work(async () =>
    {
        var id = RequireGame();
        var api = nexus ?? throw new ModManagerException("Connect Nexus first.");
        var user = account ?? throw new ModManagerException("Connect Nexus first.");
        var link = resolvedMod ?? new NexusModLink("cyberpunk2077", file.ModId);
        cancellation = new CancellationTokenSource();
        var uri = await api.DownloadAsync(link with { FileId = file.FileId }, user, cancellation.Token);
        var path = DownloadPath();
        await Downloads.FetchAsync(http, uri, path, Progress(), cancellation.Token);
        var mod = await Task.Run(() => manager.Stager.Stage(path, id, file.Name));
        staged.Clear(); staged.Add(mod); batchId = null; replaceConflicts = false;
        status.Text = "Downloaded · review staged files";
    });

    private string DownloadPath() => Path.Combine(manager.Store.DirectoryPath, "downloads", Guid.NewGuid().ToString("N") + ".zip");
    private IProgress<double> Progress() => new Progress<double>(value => { progress.Value = value; status.Text = $"Downloading… {value:F0}%"; });

    private Task LoadRemoteManifest() => Work(async () =>
    {
        var api = nexus ?? throw new ModManagerException("Connect Nexus first.");
        var selected = collection ?? throw new ModManagerException("Resolve a collection first.");
        cancellation = new CancellationTokenSource();
        var uri = await api.CollectionDownloadAsync(selected, cancellation.Token); var path = DownloadPath();
        await Downloads.FetchAsync(http, uri, path, Progress(), cancellation.Token);
        using var stream = new MemoryStream();
        await Task.Run(() =>
        {
            var manifests = SevenZip.Inventory(path).Where(entry => entry.Path == "collection.json").ToArray();
            if (manifests.Length != 1) throw new ModManagerException("Invalid collection manifest archive.");
            SevenZip.Extract(path, manifests[0], stream, 8 * 1024 * 1024);
        });
        using var document = JsonDocument.Parse(stream.ToArray());
        var plan = Collections.Parse(document.RootElement);
        var expected = selected.Files.Select(file => (file.ModId, file.FileId)).ToHashSet();
        if (plan.Entries.Any(entry => !expected.Contains((entry.ModId, entry.FileId))) || expected.Count != plan.Entries.Count)
            throw new ModManagerException("Collection manifest does not match the resolved revision.");
        collectionPlan = plan; batchId = selected.Slug + "@" + selected.Revision;
        status.Text = "Manifest checked · review required and optional mods";
    });

    private Task ImportManifest() => Work(async () =>
    {
        RequireGame();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import collection.json", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Collection JSON") { Patterns = ["*.json"] }] });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath() ?? throw new ModManagerException("Choose a local manifest.");
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new ModManagerException("Manifest too large.");
        using var stream = File.OpenRead(path); using var document = await JsonDocument.ParseAsync(stream);
        collectionPlan = Collections.Parse(document.RootElement); collection = null;
        batchId = collectionPlan.Name + "@local-" + Guid.NewGuid().ToString("N")[..8]; page = "Downloads";
    });

    private Task DownloadCollection() => Work(async () =>
    {
        var id = RequireGame(); var api = nexus ?? throw new ModManagerException("Connect Nexus first.");
        var user = account ?? throw new ModManagerException("Connect Nexus first.");
        if (!user.Premium) throw new ModManagerException("Automatic collection downloads require Premium. Individual authorized nxm files can be imported separately.");
        var plan = collectionPlan ?? throw new ModManagerException("Check a collection manifest first.");
        var incoming = new List<ManagedMod>(); cancellation = new CancellationTokenSource();
        foreach (var entry in plan.Entries.Where(entry => !entry.Optional || includeOptional))
        {
            var uri = await api.DownloadAsync(new NexusModLink("cyberpunk2077", entry.ModId, entry.FileId), user, cancellation.Token);
            var path = DownloadPath(); await Downloads.FetchAsync(http, uri, path, Progress(), cancellation.Token);
            await Task.Run(() => Collections.VerifyDownload(path, entry));
            incoming.Add(await Task.Run(() => manager.Stager.Stage(path, id, entry.Name, batchId)));
        }
        if (incoming.Count == 0) throw new ModManagerException("No collection files selected.");
        staged.Clear(); staged.AddRange(incoming); replaceConflicts = false;
        status.Text = "Collection staged · review all destination paths";
    });

    private void Notify(Activity activity)
    {
        notifications?.Show(new Notification(activity.Title, activity.Message, NotificationType.Success,
            TimeSpan.FromSeconds(8), () => Activate(activity)));
        if (!quiet && (!IsActive || WindowState == WindowState.Minimized)) _ = NativeNotification(activity);
    }

    private async Task NativeNotification(Activity activity)
    {
        try
        {
            var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--app-name=Vortnux", "--wait", "--expire-time=15000", "--action=open=Open", "--", activity.Title, activity.Message }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string response;
            try
            {
                response = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            { if (!process.HasExited) process.Kill(); return; }
            if (response.Trim() == "open") Dispatcher.UIThread.Post(() => Activate(activity));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        { /* Native notifications are optional; persistent activity and in-app toast remain available. */ }
    }
}
