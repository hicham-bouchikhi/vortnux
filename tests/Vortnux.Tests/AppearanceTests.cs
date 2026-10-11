using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vortnux.Desktop;

namespace Vortnux.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public void PreferencesRoundTripAndInvalidValuesFallBack()
    {
        using var fixture = new Fixture();
        var directory = fixture.Manager.Store.DirectoryPath;
        Assert.Equal(new AppearanceSettings(), AppearanceSettings.Load(directory));
        var saved = new AppearanceSettings("Light", "pink");
        saved.Save(directory);
        Assert.Equal(saved, AppearanceSettings.Load(directory));
        File.WriteAllText(Path.Combine(directory, "appearance.json"), "{\"mode\":\"unknown\",\"accent\":\"unknown\"}");
        Assert.Equal(new AppearanceSettings(), AppearanceSettings.Load(directory));
        File.WriteAllText(Path.Combine(directory, "appearance.json"), "broken json");
        Assert.Equal(new AppearanceSettings(), AppearanceSettings.Load(directory));
    }

    [AvaloniaFact]
    public void EveryAccentAndModeUpdatesExistingControlsAndSurvivesRestart()
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            window.Navigate("Settings");
            var mode = Find<ComboBox>(window, "AppearanceMode");
            var key = Find<TextBox>(window, "NexusApiKey"); key.Text = "unfinished input";
            mode.SelectedIndex = 1;
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            var darkBackground = ((SolidColorBrush)window.Background!).Color;
            foreach (var accent in AppearanceSettings.Accents)
            {
                var button = Find<Button>(window, "Accent_" + accent.Id);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Color.Parse(accent.Dark), ((SolidColorBrush)button.BorderBrush!).Color);
                Assert.Equal(2, button.BorderThickness.Left);
                Assert.Equal(accent.Id, AppearanceSettings.Load(fixture.Manager.Store.DirectoryPath).Accent);
            }
            mode.SelectedIndex = 2;
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.NotEqual(darkBackground, ((SolidColorBrush)window.Background!).Color);
            Assert.Equal("unfinished input", key.Text);
            Assert.Equal("Settings", window.CurrentPage);
            var crimson = Find<Button>(window, "Accent_red");
            Assert.Equal(Color.Parse(AppearanceSettings.Accents.Last().Light), ((SolidColorBrush)crimson.BorderBrush!).Color);
            SavePreview(window, "appearance-light.png");
            mode.SelectedIndex = 1;
            Find<Button>(window, "Accent_cyber").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SavePreview(window, "appearance-dark.png");
        }
        finally { window.Close(); }
        var reopened = new MainWindow(fixture.Manager);
        try { Assert.Equal(ThemeVariant.Dark, reopened.RequestedThemeVariant); }
        finally { reopened.Close(); }
    }

    [AvaloniaFact]
    public void FollowSystemInheritsThemeChangesWhileExplicitModeStaysFixed()
    {
        using var fixture = new Fixture();
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            window.Navigate("Settings");
            var mode = Find<ComboBox>(window, "AppearanceMode");
            Assert.Equal(0, mode.SelectedIndex);
            app.RequestedThemeVariant = ThemeVariant.Dark;
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            var dark = ((SolidColorBrush)window.Background!).Color;
            app.RequestedThemeVariant = ThemeVariant.Light;
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.NotEqual(dark, ((SolidColorBrush)window.Background!).Color);
            mode.SelectedIndex = 1;
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            app.RequestedThemeVariant = ThemeVariant.Dark;
            app.RequestedThemeVariant = ThemeVariant.Light;
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            mode.SelectedIndex = 0;
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal("System", AppearanceSettings.Load(fixture.Manager.Store.DirectoryPath).Mode);
        }
        finally { window.Close(); app.RequestedThemeVariant = original; }
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void SavePreview(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
