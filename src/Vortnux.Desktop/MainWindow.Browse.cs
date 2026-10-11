using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Vortnux.Core;

namespace Vortnux.Desktop;

public sealed partial class MainWindow
{
    private CatalogSession? catalogSession;
    private CatalogKind catalogKind;
    private CatalogSort catalogSort;
    private string catalogText = "";
    private CatalogItem? selectedCatalogItem;
    private string modFilter = "";
    private int modStateFilter;
    private string collectionFilter = "";
    private bool installationRequested;

    private void OpenBrowse(CatalogKind kind)
    {
        catalogKind = kind;
        Navigate("Browse");
        if (catalogSession is { } session && session.Query.Kind != kind) _ = SearchCatalog();
    }

    private void RenderBrowse()
    {
        if (catalogSession is null)
        {
            body.Children.Add(Card("Discover Cyberpunk 2077 mods and collections", "Connect your Nexus account to browse, search and choose files to install.",
                Button("Connect Nexus account", () => Navigate("Settings"))));
            return;
        }
        var search = new TextBox { Name = "NexusSearch", Text = catalogText, PlaceholderText = "Search by name · e.g. RED4ext", MaxLength = 200, MinWidth = 300 };
        search.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) catalogText = search.Text ?? ""; };
        search.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; _ = SearchCatalog(); } };
        var sort = new ComboBox { Name = "NexusSort", ItemsSource = new[] { "Newest", "Recently updated", "Most downloaded", "Most endorsed" }, SelectedIndex = (int)catalogSort, MinWidth = 190 };
        sort.SelectionChanged += (_, _) =>
        {
            if (sort.SelectedIndex < 0) return;
            catalogSort = (CatalogSort)sort.SelectedIndex; _ = SearchCatalog();
        };
        var mods = Button("Browse mods", () => OpenBrowse(CatalogKind.Mods));
        var collections = Button("Browse collections", () => OpenBrowse(CatalogKind.Collections));
        mods.Background = Brush(catalogKind == CatalogKind.Mods ? "Selected" : "Control");
        collections.Background = Brush(catalogKind == CatalogKind.Collections ? "Selected" : "Control");
        body.Children.Add(Card("Nexus Mods · Cyberpunk 2077", $"Connected as {account?.Name} · available {(catalogKind == CatalogKind.Mods ? "mods" : "published collections")}",
            Row(mods, collections), Row(search, Button("Search", () => _ = SearchCatalog()), Button("Clear", () => { catalogText = ""; _ = SearchCatalog(); })),
            Row(Text("Sort by"), sort)));
        if (catalogSession.Loading)
        {
            body.Children.Add(Card("Searching Nexus…", "You can change the search or leave this page while it loads.",
                Button("Cancel search", () => { catalogSession.Cancel(); Render(); })));
            return;
        }
        if (catalogSession.Error is { } error)
        {
            body.Children.Add(Card("Search unavailable", error, Button("Retry search", () => _ = SearchCatalog(refresh: true))));
            return;
        }
        if (catalogSession.Result is not { } result) return;
        var query = catalogSession.Query;
        var previous = Button("Previous page", () => _ = SearchCatalog(Math.Max(0, query.Offset - query.Count)));
        var next = Button("Next page", () => _ = SearchCatalog(query.Offset + query.Count));
        previous.IsEnabled = query.Offset > 0;
        next.IsEnabled = result.Items.Count > 0 && query.Offset + result.Items.Count < result.Total;
        body.Children.Add(Row(Text($"{result.Total:N0} results · page {query.Offset / query.Count + 1}"), previous, next));
        if (result.Items.Count == 0) body.Children.Add(Text("No results. Try a different name or clear your search.", 16, "Muted"));
        var cards = CardGrid("NexusCardGrid");
        body.Children.Add(cards);
        foreach (var item in result.Items)
        {
            var captured = item;
            var card = WithThumbnail(Card(item.Name, $"By {item.Author} · {item.Downloads:N0} downloads · {item.Endorsements:N0} endorsements\n{item.Summary}",
                CatalogActions(Button("Install", () => _ = BeginCatalogInstallation(captured)),
                    Button(item.Kind == CatalogKind.Mods ? "View files" : "View collection", () => _ = OpenCatalogItem(captured)),
                    Button("Open on Nexus", () => OpenWebsite(captured.Url)))), item, vertical: true);
            card.Width = 360; cards.Children.Add(card);
            if (card.Child is Grid layout && layout.Children.OfType<StackPanel>().FirstOrDefault() is { } content)
            {
                var text = content.Children.OfType<TextBlock>().ToArray();
                text[0].MaxLines = 2; text[1].MaxLines = 4;
                foreach (var block in text) block.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            }
        }
    }

    private Task SearchCatalog(int offset = 0, bool refresh = false)
    {
        if (catalogSession is null || busy && !backgroundOperation) return Task.CompletedTask;
        if (catalogSession.Query.Kind != catalogKind || catalogSession.Query.Text != catalogText.Trim() || catalogSession.Query.Sort != catalogSort) offset = 0;
        scroll.Offset = default;
        return catalogSession.SearchAsync(new CatalogQuery(catalogKind, catalogText.Trim(), catalogSort, offset), refresh);
    }

    private static WrapPanel CatalogActions(params Control[] items)
    {
        var panel = new WrapPanel();
        foreach (var item in items) { item.Margin = new Avalonia.Thickness(0, 0, 10, 8); panel.Children.Add(item); }
        return panel;
    }

    private async Task BeginCatalogInstallation(CatalogItem item)
    {
        if (busy) return;
        if (gameId is null)
        {
            Navigate("Games"); status.Text = "Add a Cyberpunk installation before installing Nexus content.";
            return;
        }
        staged.Clear(); batchId = null; replaceConflicts = false;
        await OpenCatalogItem(item, install: true);
        if (item.Kind == CatalogKind.Collections && collection is not null && collectionPlan is null) await LoadRemoteManifest();
    }

    private async Task OpenCatalogItem(CatalogItem item, bool install = false)
    {
        if (busy) return;
        installationRequested = install;
        selectedCatalogItem = item; linkText = item.Url;
        Navigate("Downloads");
        await Resolve();
    }

    private void OpenWebsite(string url)
    {
        try
        {
            _ = NexusLinks.Parse(url); // Only validated Nexus URLs reach the desktop opener.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error) when (error is ModManagerException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { status.Text = "Could not open Nexus in your browser: " + error.Message; }
    }
}
