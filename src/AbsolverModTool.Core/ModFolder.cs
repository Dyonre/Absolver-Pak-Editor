using System.IO.Compression;
using System.Text;

namespace AbsolverModTool.Core;

/// <summary>
/// One self-contained folder per mod, under <see cref="Config.WorkDir"/> (which sits next to the tool by default):
/// <code>
/// work/MyMod/
///   MyMod.recipe.json     the edits (what the tool replays to rebuild)
///   build/                the edited assets the pak was packed from (kept so a rebuild/verify has everything; not released)
///   zzzMyMod.pak (+.sig)  the mod itself (the .sig is only for local Deploy; the zip leaves it out)
///   README.txt            install steps + a list of what it changes
///   MyMod.zip             the release: pak, recipe and README
/// </code>
/// </summary>
public static class ModFolder
{
    public record Layout(string Name, string Root, string Recipe, string Build, string Pak, string Sig, string Readme, string Zip);

    public static string CleanName(string modName)
    {
        var n = modName.Trim();
        if (n.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (n.StartsWith("zzz", StringComparison.OrdinalIgnoreCase) && n.Length > 3) n = n[3..];   // the pak gets the zzz prefix itself
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        if (n.Length == 0 || n is "." or "..") throw new ArgumentException("mod name is empty or not usable as a folder name");
        return n;
    }

    public static Layout For(string modName, string? workDir = null)
    {
        var name = CleanName(modName);
        var root = Path.Combine(workDir ?? Config.WorkDir, name);
        var pak = Path.Combine(root, $"zzz{name}.pak");
        return new Layout(name, root, Path.Combine(root, $"{name}.recipe.json"), Path.Combine(root, "build"), pak,
            Path.ChangeExtension(pak, ".sig"), Path.Combine(root, "README.txt"), Path.Combine(root, $"{name}.zip"));
    }

    public static string BuildReadme(Layout l, Recipe recipe, DateTime? when = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{l.Name} - an Absolver mod");
        sb.AppendLine($"Built {(when ?? DateTime.Now):yyyy-MM-dd HH:mm} with AbsolverModTool");
        sb.AppendLine();
        sb.AppendLine("INSTALL");
        sb.AppendLine($"  1. Copy {Path.GetFileName(l.Pak)} into");
        sb.AppendLine(@"     <Steam>\steamapps\common\Absolver\Absolver\Content\Paks");
        sb.AppendLine($"  2. In that same folder, copy pakchunk0-WindowsNoEditor.sig and rename the copy to {Path.GetFileName(l.Sig)}");
        sb.AppendLine("     (every pak needs a .sig with its own name beside it; it is the game's own file, so it is not shipped in this zip).");
        sb.AppendLine("     AbsolverModTool's Deploy button does both steps for you.");
        sb.AppendLine("  3. Start the game.");
        sb.AppendLine();
        sb.AppendLine("UNINSTALL");
        sb.AppendLine($"  Delete {Path.GetFileName(l.Pak)} and {Path.GetFileName(l.Sig)} from that Paks folder.");
        sb.AppendLine();
        sb.AppendLine("FILES");
        sb.AppendLine($"  {Path.GetFileName(l.Pak)}      the mod");
        sb.AppendLine($"  {Path.GetFileName(l.Recipe)}   the list of edits below; AbsolverModTool can load it (Recipe > Load) to rebuild or change the mod");
        sb.AppendLine();
        sb.AppendLine($"CHANGES ({recipe.Edits.Count})");
        foreach (var e in recipe.Edits.Take(300)) sb.AppendLine("  " + e);
        if (recipe.Edits.Count > 300) sb.AppendLine($"  ... and {recipe.Edits.Count - 300} more (see the recipe file)");
        sb.AppendLine();
        sb.AppendLine("The pak contains only edited game data written by the tool; no unedited game files are included.");
        return sb.ToString();
    }

    /// <summary>Writes README.txt and zips pak + recipe + README into <see cref="Layout.Zip"/> (replacing an older zip).
    /// Left out on purpose: build/ (the pak already holds those assets) and the .sig, which is a copy of the base game's own
    /// pakchunk0 signature file (Packer makes it), so it is not redistributed - the README tells the user to copy it.</summary>
    public static string Zip(Layout l, Recipe recipe, Action<string>? log = null)
    {
        if (!File.Exists(l.Pak)) throw new FileNotFoundException($"no pak to release yet: {l.Pak}");
        File.WriteAllText(l.Readme, BuildReadme(l, recipe));
        if (!File.Exists(l.Recipe)) RecipeIO.Save(l.Recipe, recipe);
        if (File.Exists(l.Zip)) File.Delete(l.Zip);

        var files = new[] { l.Pak, l.Recipe, l.Readme }.Where(File.Exists).ToList();
        using (var zip = ZipFile.Open(l.Zip, ZipArchiveMode.Create))
            foreach (var f in files) zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
        log?.Invoke($"zipped {files.Count} file(s) -> {l.Zip} ({new FileInfo(l.Zip).Length / 1024} KB)");
        return l.Zip;
    }

    /// <summary>Removes everything a Create step made (build folder, pak, sig, README, zip) but keeps the recipe.</summary>
    public static void Clean(Layout l)
    {
        if (Directory.Exists(l.Build)) Directory.Delete(l.Build, true);
        foreach (var f in new[] { l.Pak, l.Sig, l.Readme, l.Zip }) if (File.Exists(f)) File.Delete(f);
    }
}
