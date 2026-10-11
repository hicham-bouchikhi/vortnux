using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vortnux.Core;

namespace Vortnux.Desktop;

public sealed partial class MainWindow
{
    private CancellationTokenSource thumbnailView = new();
    private readonly List<Bitmap> visibleBitmaps = [];

    private void ClearThumbnailViews()
    {
        thumbnailView.Cancel(); thumbnailView.Dispose();
        thumbnailView = new CancellationTokenSource();
        foreach (var bitmap in visibleBitmaps) bitmap.Dispose();
        visibleBitmaps.Clear();
        if (closed) thumbnailView.Dispose();
    }

    private Border WithThumbnail(Border card, CatalogItem item, bool vertical = false)
    {
        var content = card.Child!; card.Child = null;
        var layout = vertical ? new Grid { RowDefinitions = new RowDefinitions("Auto,*") } : new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
        var image = new Image { Name = "CatalogThumbnail", Width = vertical ? 320 : 200, Height = vertical ? 180 : 132, Stretch = Stretch.UniformToFill };
        AutomationProperties.SetName(image, item.Name + " preview");
        var fallback = Text(CatalogImages.IsAllowed(item.ImageUrl) ? "Loading image…" : "No image available", 13, "#ADB4C9");
        fallback.HorizontalAlignment = HorizontalAlignment.Center; fallback.VerticalAlignment = VerticalAlignment.Center;
        var preview = new Grid(); preview.Children.Add(image); preview.Children.Add(fallback);
        layout.Children.Add(new Border { Child = preview, Background = Brush("#282D40"), CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Width = vertical ? 320 : 200, Height = vertical ? 180 : 132, VerticalAlignment = VerticalAlignment.Top });
        if (vertical) { Grid.SetRow(content, 1); content.Margin = new Thickness(0, 16, 0, 0); }
        else { Grid.SetColumn(content, 1); content.Margin = new Thickness(18, 0, 0, 0); }
        layout.Children.Add(content);
        card.Child = layout;
        if (CatalogImages.IsAllowed(item.ImageUrl)) _ = LoadThumbnail(item.ImageUrl!, image, fallback, thumbnailView.Token);
        return card;
    }

    private async Task LoadThumbnail(string url, Image image, TextBlock fallback, CancellationToken token)
    {
        Bitmap? bitmap = null;
        try
        {
            var bytes = await catalogImages.FetchAsync(url, token);
            if (token.IsCancellationRequested || closed) return;
            if (bytes is null) { fallback.Text = "Image unavailable"; return; }
            bitmap = await Task.Run(() =>
            {
                using var header = new MemoryStream(bytes);
                using var codec = SkiaSharp.SKCodec.Create(header);
                if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || codec.Info.Width > 8192 || codec.Info.Height > 8192 ||
                    (long)codec.Info.Width * codec.Info.Height > 16_000_000 || (long)codec.Info.Height * 400 / codec.Info.Width > 4096)
                    throw new InvalidDataException("Invalid image dimensions.");
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, 400);
            }, token);
            if (token.IsCancellationRequested || closed) { bitmap.Dispose(); return; }
            if (bitmap.PixelSize.Height > 4096) { bitmap.Dispose(); fallback.Text = "Image unavailable"; return; }
            visibleBitmaps.Add(bitmap); image.Source = bitmap; fallback.IsVisible = false;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            bitmap?.Dispose();
            if (!token.IsCancellationRequested && !closed) fallback.Text = "Image unavailable";
        }
    }
}
