using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Vortnux.Desktop;

public sealed partial class MainWindow
{
    private AppearanceSettings appearance = new();
    private readonly Dictionary<string, SolidColorBrush> themeBrushes = [];
    private readonly Dictionary<string, Button> accentButtons = [];
    private TextBlock? themeDescription;

    private void InitializeAppearance()
    {
        foreach (var token in new[] { "Canvas", "Sidebar", "Card", "Control", "Preview", "Selected", "Accent", "Text", "Muted", "Outline" })
            themeBrushes[token] = new SolidColorBrush();
        ActualThemeVariantChanged += (_, _) => UpdateAppearanceColors();
        ApplyAppearance();
        var buttons = new Style(selector => selector.OfType<Button>());
        buttons.Setters.Add(new Setter(Avalonia.Controls.Button.BackgroundProperty, Brush("Control")));
        buttons.Setters.Add(new Setter(Avalonia.Controls.Button.ForegroundProperty, Brush("Text")));
        buttons.Setters.Add(new Setter(Avalonia.Controls.Button.BorderBrushProperty, Brush("Outline")));
        buttons.Setters.Add(new Setter(Avalonia.Controls.Button.BorderThicknessProperty, new Thickness(1)));
        Styles.Add(buttons);
    }

    private void ApplyAppearance()
    {
        RequestedThemeVariant = appearance.Mode switch
        {
            "Dark" => ThemeVariant.Dark,
            "Light" => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
        // Default inherits the platform preference. Avalonia raises the actual-theme
        // event on subsequent desktop changes, keeping our custom surfaces in sync.
        UpdateAppearanceColors();
    }

    private void UpdateAppearanceColors()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var accent = AppearanceSettings.Accents.First(item => item.Id == appearance.Accent);
        var accentColor = Color.Parse(dark ? accent.Dark : accent.Light);
        var canvas = Color.Parse(dark ? "#0B0E16" : "#F1F4FA");
        var colors = new Dictionary<string, Color>
        {
            ["Canvas"] = canvas,
            ["Sidebar"] = Color.Parse(dark ? "#111724" : "#E5EBF5"),
            ["Card"] = Color.Parse(dark ? "#171F2E" : "#FFFFFF"),
            ["Control"] = Color.Parse(dark ? "#222D40" : "#E8EEF7"),
            ["Preview"] = Color.Parse(dark ? "#202B3D" : "#DEE6F2"),
            ["Text"] = Color.Parse(dark ? "#F0F5FF" : "#17233B"),
            ["Muted"] = Color.Parse(dark ? "#A6B5CF" : "#52627D"),
            ["Outline"] = Color.Parse(dark ? "#35435A" : "#CCD6E6"),
            ["Accent"] = accentColor,
            ["Selected"] = Mix(canvas, accentColor, dark ? .18 : .12)
        };
        foreach (var item in colors) themeBrushes[item.Key].Color = item.Value;
        if (Application.Current?.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            SetFluentAccent(fluent, ThemeVariant.Dark, Color.Parse(accent.Dark));
            SetFluentAccent(fluent, ThemeVariant.Light, Color.Parse(accent.Light));
        }
        foreach (var item in accentButtons)
        {
            item.Value.BorderBrush = item.Key == appearance.Accent ? Brush("Accent") : Brush("Outline");
            item.Value.BorderThickness = new Thickness(item.Key == appearance.Accent ? 2 : 1);
        }
        if (themeDescription is not null)
            themeDescription.Text = appearance.Mode == "System"
                ? $"Following your desktop · {(dark ? "dark" : "light")} mode"
                : $"{appearance.Mode} mode · desktop sync is off";
    }

    private static void SetFluentAccent(FluentTheme fluent, ThemeVariant variant, Color color)
    {
        if (!fluent.Palettes.TryGetValue(variant, out var palette))
            fluent.Palettes[variant] = new ColorPaletteResources { Accent = color };
        else palette.Accent = color;
    }

    private static Color Mix(Color background, Color foreground, double amount) => Color.FromRgb(
        (byte)(background.R + (foreground.R - background.R) * amount),
        (byte)(background.G + (foreground.G - background.G) * amount),
        (byte)(background.B + (foreground.B - background.B) * amount));

    private void ChangeAppearance(AppearanceSettings value)
    {
        appearance = value;
        ApplyAppearance();
        try
        {
            appearance.Save(manager.Store.DirectoryPath);
            status.Text = "Appearance saved";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = "Appearance applied, but could not save it: " + error.Message;
        }
    }

    private void RenderAppearanceSettings()
    {
        var mode = new ComboBox
        {
            Name = "AppearanceMode", ItemsSource = new[] { "Follow system", "Dark", "Light" },
            SelectedIndex = appearance.Mode switch { "Dark" => 1, "Light" => 2, _ => 0 },
            MinWidth = 200
        };
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex < 0) return;
            ChangeAppearance(appearance with { Mode = mode.SelectedIndex switch { 1 => "Dark", 2 => "Light", _ => "System" } });
        };
        themeDescription = Text("", 13, "Muted");
        var swatches = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        accentButtons.Clear();
        foreach (var accent in AppearanceSettings.Accents)
        {
            var swatch = new Border
            {
                Background = new SolidColorBrush(Color.Parse(accent.Dark)), Width = 16, Height = 16,
                CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center
            };
            var button = Button(accent.Name, () => ChangeAppearance(appearance with { Accent = accent.Id }));
            button.Name = "Accent_" + accent.Id;
            button.Content = Row(swatch, Text(accent.Name, 13));
            button.MinWidth = 170;
            accentButtons.Add(accent.Id, button);
            swatches.Children.Add(button);
        }
        body.Children.Add(Card("Appearance", "Build your loadout: neon accents, crisp panels, and a theme that fits your setup.",
            Row(Text("Theme", 14), mode), themeDescription, Text("Accent color", 14), swatches));
        UpdateAppearanceColors();
    }
}
