using System.Text.Json;

namespace AbsolverModTool.Core;

public static class Config
{
    public static string UnrealPakPath { get; set; } = @"C:\Program Files\Epic Games\UE_4.18\Engine\Binaries\Win64\UnrealPak.exe";
    public static string GamePaksDir { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\Absolver\Absolver\Content\Paks";
    /// <summary>Folder the tool runs from. Everything it makes (work/, recipes/, logs/, settings.json) lives under it, so a release is one folder you can move or zip.</summary>
    public static string AppDir { get; } = AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Where each mod gets its own folder (recipe, build, pak, README, zip) - see <see cref="ModFolder"/>. Defaults to work/ next to the tool.</summary>
    public static string WorkDir { get; set; } = Path.Combine(AppDir, "work");

    /// <summary>Default place for hand-saved recipes (Recipe > Save/Load).</summary>
    public static string RecipesDir => Path.Combine(AppDir, "recipes");

    /// <summary>A relative setting means "relative to the tool", not to whatever folder it was started from.</summary>
    public static string FromApp(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(path, AppDir);
    public const string MountPrefix = "../../../Absolver/Content/";

    /// <summary>The folder of extracted game assets the Data Editor reads. Set ABSOLVER_VANILLA_DIR or use Open Folder.</summary>
    public static readonly string DefaultVanillaDir = FindDefaultVanillaDir();

    static string FindDefaultVanillaDir()
    {
        var env = Environment.GetEnvironmentVariable("ABSOLVER_VANILLA_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        foreach (var name in new[] { "GameExtract_Full", "GameExtract" })
            for (var d = new DirectoryInfo(AppDir); d != null; d = d.Parent)
            {
                var c = Path.Combine(d.FullName, name);
                if (Directory.Exists(c)) return c;
            }
        return Path.Combine(AppDir, "GameExtract");   // not there yet: the tool asks for a folder
    }
}

/// <summary>The subset of Config that's actually machine-specific and worth persisting - so a
/// different UE engine install path, Steam library, or preferred output location doesn't need a
/// recompile.</summary>
public class AppSettings
{
    public string? UnrealPakPath { get; set; }
    public string? GamePaksDir { get; set; }
    public string? WorkDir { get; set; }
}

public static class SettingsIO
{
    public static readonly string DefaultPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>Loads settings.json next to the executable (if present) and applies it to
    /// Config. Safe to call even when the file doesn't exist - Config keeps its defaults.</summary>
    public static void LoadAndApply(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return;

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        if (settings == null) return;
        if (!string.IsNullOrWhiteSpace(settings.UnrealPakPath)) Config.UnrealPakPath = settings.UnrealPakPath;
        if (!string.IsNullOrWhiteSpace(settings.GamePaksDir)) Config.GamePaksDir = settings.GamePaksDir;
        if (!string.IsNullOrWhiteSpace(settings.WorkDir)) Config.WorkDir = Config.FromApp(settings.WorkDir);
    }

    public static AppSettings CurrentAsSettings() => new()
    {
        UnrealPakPath = Config.UnrealPakPath,
        GamePaksDir = Config.GamePaksDir,
        WorkDir = Config.WorkDir,
    };

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
