using System.Text.Json;

namespace Vortnux.Desktop;

public sealed record GamerAccent(string Id, string Name, string Dark, string Light);

public sealed record AppearanceSettings(string Mode = "System", string Accent = "cyber")
{
    public static IReadOnlyList<GamerAccent> Accents { get; } =
    [
        new("cyber", "Cyber cyan", "#22D3EE", "#007A91"),
        new("violet", "Arcade violet", "#A78BFA", "#7040CC"),
        new("lime", "Toxic green", "#A3E635", "#477500"),
        new("pink", "Neon pink", "#F472B6", "#B52472"),
        new("orange", "Solar orange", "#FB923C", "#B74D00"),
        new("red", "Crimson red", "#FB7185", "#BE2440")
    ];

    public static AppearanceSettings Load(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "appearance.json")));
            var root = document.RootElement;
            var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var accent = root.TryGetProperty("accent", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            return new(mode is "System" or "Dark" or "Light" ? mode : "System",
                Accents.Any(item => item.Id == accent) ? accent! : "cyber");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new();
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appearance.json");
        var temporary = path + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteString("mode", Mode); writer.WriteString("accent", Accent); writer.WriteEndObject();
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
