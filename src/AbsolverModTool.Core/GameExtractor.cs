namespace AbsolverModTool.Core;

/// <summary>Extracts the installed base game into the folder the Data Editor reads.</summary>
public static class GameExtractor
{
    // The ASCII AES key used by the UE 4.18 UnrealPak command; do not use the hex documentation form.
    const string AesKey = "c7B2DvPsRgH9Ve0BjGJKzBIeCIeoSTtX";

    public static IReadOnlyList<string> BasePaks(string paksDir) =>
        Directory.GetFiles(paksDir, "pakchunk*-WindowsNoEditor.pak")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Extracts only the installed base pakchunks. The destination must be empty to prevent accidental mixing with an older extraction.</summary>
    public static void ExtractBaseGame(string paksDir, string destination, Action<string>? log = null)
    {
        if (!File.Exists(Config.UnrealPakPath)) throw new FileNotFoundException("UnrealPak.exe was not found. Set it in Settings.", Config.UnrealPakPath);
        if (!Directory.Exists(paksDir)) throw new DirectoryNotFoundException($"Game Paks folder was not found: {paksDir}");
        var paks = BasePaks(paksDir);
        if (paks.Count == 0) throw new FileNotFoundException("No base pakchunk*-WindowsNoEditor.pak files were found in the configured Paks folder.");
        foreach (var pak in paks)
            ExtractPak(pak, destination, log);
    }

    /// <summary>Extracts one mod pak into an empty, editable source folder.</summary>
    public static void ExtractPak(string pak, string destination, Action<string>? log = null)
    {
        if (!File.Exists(Config.UnrealPakPath)) throw new FileNotFoundException("UnrealPak.exe was not found. Set it in Settings.", Config.UnrealPakPath);
        if (!File.Exists(pak)) throw new FileNotFoundException("Pak file was not found.", pak);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("The editable source folder is not empty.");
        Directory.CreateDirectory(destination);
        log?.Invoke($"Extracting {Path.GetFileName(pak)}...");
        var result = UnrealPak.Run($"\"{pak}\" -Extract \"{destination}\" -aes={AesKey}");
        if (result.ExitCode != 0) throw new InvalidOperationException($"UnrealPak failed on {Path.GetFileName(pak)} (exit {result.ExitCode}): {result.Output.Trim()}");
        log?.Invoke($"Extracted {Path.GetFileName(pak)}");
    }
}
