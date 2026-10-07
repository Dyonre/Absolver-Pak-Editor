using System.Collections.ObjectModel;
using System.Text.Json;

namespace AbsolverModTool.Core;

// Recipes: edits are JSON descriptions (asset, row, property, value), applied to a fresh
// copy of the vanilla extract at pack time. Never mutate testdata/ in place.

public class Edit
{
    public string Op { get; set; } = "";           // "set-prop" | "clone-row" | "add-array-element" | "retime-anim"
    public string Asset { get; set; } = "";
    public string? Row { get; set; }                // set-prop/add-array-element target row
    public string? Property { get; set; }            // set-prop/add-array-element target property
    public string? Value { get; set; }                // set-prop/add-array-element new value (string form)
    public string? SourceRow { get; set; }            // clone-row source
    public string? NewRow { get; set; }               // clone-row destination

    public override string ToString() => Op switch
    {
        "set-prop" => $"set-prop: {Asset} [{Row}] {Property} = {Value}",
        "clone-row" => $"clone-row: {Asset} [{SourceRow}] -> [{NewRow}]",
        "add-array-element" => $"add-array-element: {Asset} [{Row}] {Property} += {Value}",
        "retime-anim" => $"retime-anim: {Asset} [{Row}] - move the animation's notifies onto the row's timeline at pack time",
        _ => $"{Op}: {Asset}",
    };
}

public class Recipe
{
    // ObservableCollection so a GUI can bind a pending-edits list directly and see removals -
    // JSON serialization works the same as a plain List<T>.
    public ObservableCollection<Edit> Edits { get; set; } = new();
}

public static class RecipeIO
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Recipe Load(string path)
    {
        if (!File.Exists(path)) return new Recipe();
        return JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), Options) ?? new Recipe();
    }

    public static void Save(string path, Recipe recipe)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(recipe, Options));
    }
}
