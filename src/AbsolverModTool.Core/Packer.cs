namespace AbsolverModTool.Core;

public record VerifyCheck(string Name, bool Ok, string Message);
public record VerifyResult(bool Ok, List<VerifyCheck> Checks);
public record PackResult(bool Ok, string PakPath, string? SigPath, VerifyResult Verify);

public static class Packer
{
    record FileEntry(string LocalUasset, string? LocalUexp, string MountUasset, string? MountUexp);

    static List<FileEntry> Enumerate(string workDir)
    {
        var entries = new List<FileEntry>();
        foreach (var uasset in Directory.GetFiles(workDir, "*.uasset", SearchOption.AllDirectories).OrderBy(p => p))
        {
            var uexp = Path.ChangeExtension(uasset, ".uexp");
            var rel = Path.GetRelativePath(workDir, uasset).Replace('\\', '/');
            var relUexp = Path.GetRelativePath(workDir, uexp).Replace('\\', '/');
            entries.Add(new FileEntry(
                Path.GetFullPath(uasset),
                File.Exists(uexp) ? Path.GetFullPath(uexp) : null,
                Config.MountPrefix + StripAbsolverContentPrefix(rel),
                File.Exists(uexp) ? Config.MountPrefix + StripAbsolverContentPrefix(relUexp) : null));
        }
        return entries;
    }

    // workDir mirrors "Absolver/Content/..." directly (same convention as testdata/Vanilla),
    // so the mount path is just Config.MountPrefix + whatever comes after "Absolver/Content/".
    static string StripAbsolverContentPrefix(string relPath)
    {
        const string marker = "Absolver/Content/";
        var idx = relPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? relPath[(idx + marker.Length)..] : relPath;
    }

    public static PackResult Pack(string workDir, string pakPath, Action<string>? log = null)
    {
        if (!Directory.Exists(workDir)) throw new DirectoryNotFoundException($"Work directory not found: {workDir}");
        pakPath = Path.GetFullPath(pakPath);
        Directory.CreateDirectory(Path.GetDirectoryName(pakPath)!);

        var entries = Enumerate(workDir);
        if (entries.Count == 0) throw new InvalidOperationException($"No .uasset files found under {workDir}");

        var listPath = Path.Combine(Path.GetTempPath(), $"modfiles_{Path.GetFileNameWithoutExtension(pakPath)}.txt");
        var lines = new List<string>();
        foreach (var e in entries)
        {
            lines.Add($"\"{e.LocalUasset}\" \"{e.MountUasset}\"");
            if (e.LocalUexp != null) lines.Add($"\"{e.LocalUexp}\" \"{e.MountUexp}\"");
        }
        File.WriteAllLines(listPath, lines);

        log?.Invoke($"Response file: {listPath} ({entries.Count} asset(s))");
        foreach (var line in lines) log?.Invoke("  " + line);

        if (File.Exists(pakPath)) File.Delete(pakPath);

        var result = UnrealPak.Run($"\"{pakPath}\" -create=\"{listPath}\"");
        log?.Invoke(result.Output);
        if (result.ExitCode != 0 || !File.Exists(pakPath))
            throw new InvalidOperationException($"UnrealPak failed (exit {result.ExitCode}) - no pak produced.");

        string? sigPath = null;
        var sigSource = Directory.GetFiles(Config.GamePaksDir, "pakchunk0-WindowsNoEditor.sig").FirstOrDefault()
            ?? Directory.GetFiles(Config.GamePaksDir, "*.sig").FirstOrDefault();
        if (sigSource != null)
        {
            sigPath = Path.ChangeExtension(pakPath, ".sig");
            File.Copy(sigSource, sigPath, true);
            log?.Invoke($"sig      {sigPath}  (copied from {Path.GetFileName(sigSource)})");
        }
        else
        {
            log?.Invoke("WARNING: no .sig found in the game's Paks folder - the pak may not load.");
        }

        log?.Invoke("");
        log?.Invoke("Verifying...");
        var verify = Verify(pakPath, workDir);
        foreach (var c in verify.Checks) log?.Invoke($"{(c.Ok ? "PASS" : "FAIL")}  {c.Message}");
        log?.Invoke(verify.Ok ? "VERIFY PASSED" : "VERIFY FAILED");

        return new PackResult(verify.Ok, pakPath, sigPath, verify);
    }

    public static VerifyResult Verify(string pakPath, string workDir)
    {
        if (!File.Exists(pakPath)) throw new FileNotFoundException($"Pak not found: {pakPath}");
        if (!Directory.Exists(workDir)) throw new DirectoryNotFoundException($"Work directory not found: {workDir}");
        pakPath = Path.GetFullPath(pakPath);

        var result = UnrealPak.Run($"\"{pakPath}\" -List");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"UnrealPak -List failed (exit {result.ExitCode}): {result.Output}");

        var lines = result.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // UnrealPak -List output looks like:
        //   LogPakFile: Display: Mount point ../../../Absolver/Content/DB/Shop/
        //   LogPakFile: Display: "EquipmentShop.uasset" offset: 0, size: 28192 bytes, sha1: ...
        // The mount point is the common directory prefix of everything in the pak, and each
        // listed filename is relative to it, not a full mount path - reconstruct the full path.
        var mountLine = lines.FirstOrDefault(l => l.Contains("Mount point", StringComparison.OrdinalIgnoreCase));
        string? mountPoint = null;
        if (mountLine != null)
        {
            var idx = mountLine.IndexOf("Mount point", StringComparison.OrdinalIgnoreCase);
            mountPoint = mountLine[(idx + "Mount point".Length)..].TrimStart(':', ' ').Trim();
        }
        bool mountOk = mountPoint != null && mountPoint.StartsWith(Config.MountPrefix, StringComparison.OrdinalIgnoreCase);

        var listedFiles = lines
            .Where(l => l.Contains("offset:") && l.Contains('"'))
            .Select(l =>
            {
                var start = l.IndexOf('"');
                var end = l.IndexOf('"', start + 1);
                return l[(start + 1)..end];
            })
            .Select(name => (mountPoint ?? "") + name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = Enumerate(workDir)
            .SelectMany(e => e.MountUexp != null ? new[] { e.MountUasset, e.MountUexp } : new[] { e.MountUasset })
            .ToList();

        var missing = expected.Where(f => !listedFiles.Contains(f)).ToList();
        bool presenceOk = missing.Count == 0;
        bool countOk = listedFiles.Count == expected.Count;

        var checks = new List<VerifyCheck>
        {
            new("mount-point", mountOk, $"mount point: {mountPoint ?? "(not found in -List output)"}"),
            new("files-present", presenceOk, presenceOk
                ? $"expected files present ({expected.Count}/{expected.Count})"
                : $"expected files present ({expected.Count - missing.Count}/{expected.Count}) - missing: {string.Join(", ", missing)}"),
            new("file-count", countOk, $"file count matches (pak has {listedFiles.Count}, expected {expected.Count})"),
        };

        return new VerifyResult(mountOk && presenceOk && countOk, checks);
    }

    public static (string Pak, string? Sig) Deploy(string pakPath, Action<string>? log = null)
    {
        if (!File.Exists(pakPath)) throw new FileNotFoundException($"Pak not found: {pakPath}");
        var sigPath = Path.ChangeExtension(pakPath, ".sig");

        var destPak = Path.Combine(Config.GamePaksDir, Path.GetFileName(pakPath));
        File.Copy(pakPath, destPak, true);
        log?.Invoke($"deployed {destPak}");

        string? destSig = null;
        if (File.Exists(sigPath))
        {
            destSig = Path.Combine(Config.GamePaksDir, Path.GetFileName(sigPath));
            File.Copy(sigPath, destSig, true);
            log?.Invoke($"deployed {destSig}");
        }
        else
        {
            log?.Invoke("WARNING: no matching .sig next to the pak - run 'pack' first, or copy one manually.");
        }

        return (destPak, destSig);
    }
}
