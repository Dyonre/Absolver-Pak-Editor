using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;

namespace AbsolverViewer.Paks;

/// <summary>One row of the attacks table found inside a pak that is being looked at, compared with the base game.</summary>
public record RowInfo(string Id, string Name, string Anim, string Status, bool AnimInPak);

/// <summary>What is inside a .pak, found without mounting it into the viewer.</summary>
public record PakInfo(string Path, string Name, long SizeBytes, int FileCount, int AnimationFiles, IReadOnlyList<string> Files, IReadOnlyList<RowInfo> Rows, string Note);

// Which pak a move comes from, mounting extra paks, and looking inside a pak before loading it.
public sealed partial class PakAnimationSource
{
    readonly List<string> _extra = new();
    string _paksDir = "";
    string _keyHex = "";
    Dictionary<string, JObject>? _baseRows;
    readonly Dictionary<string, Dictionary<string, JObject>> _rowsPerPak = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paks added with <see cref="AddPak"/> (full paths), mounted after the Paks folder so they win.</summary>
    public IReadOnlyList<string> ExtraPaks => _extra;

    static bool IsBase(string pakFile) => System.IO.Path.GetFileName(pakFile).StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Already mounted, either from the Paks folder (same file name) or added earlier.</summary>
    public bool IsLoaded(string pakPath)
    {
        var name = System.IO.Path.GetFileName(pakPath);
        return MountedPaks.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
            || _extra.Any(p => string.Equals(System.IO.Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mounts one more pak on top of everything already mounted (its files override the same paths). Returns a one-line result.</summary>
    public string AddPak(string pakPath)
    {
        lock (_lock)
        {
            if (!File.Exists(pakPath)) throw new FileNotFoundException(pakPath);
            if (IsLoaded(pakPath)) return $"{System.IO.Path.GetFileName(pakPath)} is already loaded";
            int before = _provider.Files.Count;
            _provider.RegisterVfs(pakPath);
            _provider.SubmitKey(new FGuid(), new FAesKey(_keyHex));
            _extra.Add(pakPath);
            _animCache.Clear(); _moves = null;           // moves + animations may now resolve differently
            Log?.Invoke($"pak: added {pakPath}: {_provider.Files.Count - before} new path(s), {_provider.Files.Count} total");
            return $"loaded {System.IO.Path.GetFileName(pakPath)} ({_provider.Files.Count - before} new paths)";
        }
    }

    DefaultFileProvider OpenIsolated(IEnumerable<string> paks)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AbsolverViewerEmptyDir");
        Directory.CreateDirectory(dir);
        var p = new DefaultFileProvider(dir, SearchOption.TopDirectoryOnly, true, new VersionContainer(EGame.GAME_UE4_18));
        p.Initialize();
        foreach (var f in paks) p.RegisterVfs(f);
        p.SubmitKey(new FGuid(), new FAesKey(_keyHex));
        return p;
    }

    static JObject RowToJson(FStructFallback row)
    {
        var o = new JObject();
        foreach (var p in row.Properties) o[p.Name.Text] = JToken.FromObject(Simplify(p.Tag?.GenericValue));
        return o;
    }

    static Dictionary<string, JObject> ReadAttacks(DefaultFileProvider p)
    {
        var rows = new Dictionary<string, JObject>();
        if (!p.Files.ContainsKey(AttacksPackage + ".uasset")) return rows;
        var table = p.LoadPackage(AttacksPackage).GetExports().OfType<UDataTable>().FirstOrDefault();
        if (table == null) return rows;
        foreach (var (name, row) in table.RowMap)
            try { rows[name.Text] = RowToJson(row); } catch { /* unreadable row: left out */ }
        return rows;
    }

    /// <summary>The attacks table of the base game alone (pakchunk* only), for telling new/changed rows from untouched ones.</summary>
    Dictionary<string, JObject> BaseRows()
    {
        if (_baseRows != null) return _baseRows;
        var basePaks = Directory.EnumerateFiles(_paksDir, "*.pak").Where(IsBase).OrderBy(x => x).ToList();
        using var p = OpenIsolated(basePaks);
        return _baseRows = ReadAttacks(p);
    }

    /// <summary>attacks rows of one non-base pak (cached); empty when that pak has no attacks table.</summary>
    Dictionary<string, JObject> RowsOf(string pakPath)
    {
        if (_rowsPerPak.TryGetValue(pakPath, out var hit)) return hit;
        using var p = OpenIsolated(new[] { pakPath });
        return _rowsPerPak[pakPath] = ReadAttacks(p);
    }

    /// <summary>Adds "Src" (pak file name, or "base") and "Status" ("base" | "new" | "changed") to each {Id, Row} of <see cref="MovesJson"/>.</summary>
    void AttachSources(JArray rows)
    {
        List<string> modPaks;
        try
        {
            modPaks = Directory.EnumerateFiles(_paksDir, "*.pak").Where(f => !IsBase(f)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Concat(_extra).ToList();
            var baseRows = BaseRows();
            var perPak = modPaks.Select(f => (name: System.IO.Path.GetFileName(f), rows: RowsOf(f))).Where(x => x.rows.Count > 0).ToList();
            foreach (var r in rows.OfType<JObject>())
            {
                var id = (string)r["Id"]!; var row = (JObject)r["Row"]!;
                string src = "base", status = "base";
                if (!baseRows.TryGetValue(id, out var b)) status = "new";
                else if (!JToken.DeepEquals(b, row)) status = "changed";
                if (status != "base")
                    src = perPak.LastOrDefault(x => x.rows.TryGetValue(id, out var pr) && JToken.DeepEquals(pr, row)).name
                          ?? perPak.LastOrDefault(x => x.rows.ContainsKey(id)).name ?? "mod pak";
                r["Src"] = src; r["Status"] = status;
            }
        }
        catch (Exception ex) { Log?.Invoke($"pak: could not work out which pak each move comes from: {ex.Message}"); }
    }

    /// <summary>Looks inside a .pak on disk without mounting it into this source: file list, and the attacks rows it carries compared
    /// with the base game (new / changed / same).</summary>
    public PakInfo Inspect(string pakPath)
    {
        var fi = new FileInfo(pakPath);
        if (!fi.Exists) throw new FileNotFoundException(pakPath);
        using var p = OpenIsolated(new[] { pakPath });
        var files = p.Files.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var anims = files.Count(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && f.Contains("/Animations/", StringComparison.OrdinalIgnoreCase));
        var rows = new List<RowInfo>();
        string note = files.Count == 0 ? "nothing could be read: the pak is empty, uses another encryption key, or is not an Absolver pak" : "";
        if (files.Count > 0)
        {
            var theirs = ReadAttacks(p);
            if (theirs.Count == 0 && note == "") note = "no attacks table in this pak (it does not add or change moves)";
            var baseRows = BaseRows();
            foreach (var (id, row) in theirs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                string status = !baseRows.TryGetValue(id, out var b) ? "new" : JToken.DeepEquals(b, row) ? "same" : "changed";
                var anim = (string?)row["m_Anim"] ?? "";
                bool inPak = anim.Length > 0 && p.Files.ContainsKey(ToPackagePath(anim) + ".uasset");
                rows.Add(new RowInfo(id, (string?)row["m_RealAttackName"] ?? id, anim, status, inPak));
            }
        }
        return new PakInfo(pakPath, fi.Name, fi.Length, files.Count, anims, files, rows, note);
    }
}
