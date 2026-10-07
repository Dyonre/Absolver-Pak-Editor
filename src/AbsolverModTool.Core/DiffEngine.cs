using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace AbsolverModTool.Core;

// Which assets differ between a base (vanilla) tree and a mod tree, and for
// DataTable/plain-object assets, which properties changed and to what.

public record PropChange(string Property, string Old, string New);
public record RowDiff(string RowKey, string Kind, List<PropChange> Changes); // Kind: added|removed|changed
public record AssetDiff(string Path, string Kind, string? Detail, List<RowDiff>? Rows, List<PropChange>? Properties);

public record DiffSummary(int Added, int Changed, int ByteOnly, int Unchanged, int Unparseable)
{
    public int Total => Added + Changed + ByteOnly + Unchanged + Unparseable;
}

public static class DiffEngine
{
    public static (DiffSummary Summary, List<AssetDiff> Results) Run(string basePath, string modPath, Action<string>? log = null)
    {
        var baseDir = ResolveToDir(basePath, "base", log);
        var modDir = ResolveToDir(modPath, "mod", log);

        var modFiles = Directory.GetFiles(modDir, "*.uasset", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        if (modFiles.Count == 0) throw new InvalidOperationException($"No .uasset files found under {modDir}");

        var results = new List<AssetDiff>();
        int added = 0, changed = 0, unchanged = 0, unparseable = 0, byteOnly = 0;

        foreach (var modUasset in modFiles)
        {
            var rel = Path.GetRelativePath(modDir, modUasset).Replace('\\', '/');
            var baseUasset = Path.Combine(baseDir, rel);

            if (!File.Exists(baseUasset))
            {
                results.Add(new AssetDiff(rel, "added", null, null, null));
                added++;
                continue;
            }

            var modUexp = Path.ChangeExtension(modUasset, ".uexp");
            var baseUexp = Path.ChangeExtension(baseUasset, ".uexp");

            if (FilesIdentical(baseUasset, modUasset) && FilesIdentical(baseUexp, modUexp))
            {
                unchanged++;
                continue;
            }

            try
            {
                var baseAsset = AssetAccess.Load(baseUasset);
                var modAsset = AssetAccess.Load(modUasset);

                var baseTable = AssetAccess.GetTable(baseAsset);
                var modTable = AssetAccess.GetTable(modAsset);
                if (baseTable != null && modTable != null)
                {
                    var rowDiffs = DiffTables(baseTable, modTable, baseAsset, modAsset);
                    if (rowDiffs.Count == 0)
                    {
                        results.Add(new AssetDiff(rel, "bytes-differ-no-property-diff", "DataTableExport", null, null));
                        byteOnly++;
                        continue;
                    }
                    results.Add(new AssetDiff(rel, "data-table-changed", null, rowDiffs, null));
                    changed++;
                    continue;
                }

                var baseNormal = AssetAccess.GetNormal(baseAsset);
                var modNormal = AssetAccess.GetNormal(modAsset);
                if (baseNormal != null && modNormal != null)
                {
                    var propDiffs = DiffFields(baseNormal.Data, modNormal.Data, baseAsset, modAsset);
                    if (propDiffs.Count == 0)
                    {
                        results.Add(new AssetDiff(rel, "bytes-differ-no-property-diff", "NormalExport", null, null));
                        byteOnly++;
                        continue;
                    }
                    results.Add(new AssetDiff(rel, "object-changed", null, null, propDiffs));
                    changed++;
                    continue;
                }

                var kindDesc = $"{baseAsset.Exports.FirstOrDefault()?.GetType().Name ?? "?"} -> {modAsset.Exports.FirstOrDefault()?.GetType().Name ?? "?"}";
                results.Add(new AssetDiff(rel, "binary-differs", kindDesc, null, null));
                changed++;
            }
            catch (Exception ex)
            {
                results.Add(new AssetDiff(rel, "parse-error", ex.Message, null, null));
                unparseable++;
            }
        }

        return (new DiffSummary(added, changed, byteOnly, unchanged, unparseable), results);
    }

    static List<RowDiff> DiffTables(DataTableExport baseTable, DataTableExport modTable, UAsset baseAsset, UAsset modAsset)
    {
        var result = new List<RowDiff>();
        var baseRows = baseTable.Table.Data.ToDictionary(r => r.Name.ToString());
        var modRows = modTable.Table.Data.ToDictionary(r => r.Name.ToString());

        foreach (var key in modRows.Keys.Except(baseRows.Keys))
            result.Add(new RowDiff(key, "added", new List<PropChange>()));
        foreach (var key in baseRows.Keys.Except(modRows.Keys))
            result.Add(new RowDiff(key, "removed", new List<PropChange>()));
        foreach (var key in baseRows.Keys.Intersect(modRows.Keys))
        {
            var changes = DiffFields(baseRows[key].Value, modRows[key].Value, baseAsset, modAsset);
            if (changes.Count > 0) result.Add(new RowDiff(key, "changed", changes));
        }
        return result;
    }

    static List<PropChange> DiffFields(List<PropertyData> baseFields, List<PropertyData> modFields, UAsset baseAsset, UAsset modAsset)
    {
        var changes = new List<PropChange>();
        // Key by (Name, ArrayIndex): static arrays (e.g. the four alternate-attack slots)
        // repeat the same Name, distinguished only by ArrayIndex.
        var baseByKey = baseFields.ToDictionary(p => (p.Name.ToString(), p.ArrayIndex));
        var modByKey = modFields.ToDictionary(p => (p.Name.ToString(), p.ArrayIndex));

        foreach (var key in baseByKey.Keys.Intersect(modByKey.Keys))
        {
            var oldVal = PropertyFormatting.Format(baseByKey[key], baseAsset);
            var newVal = PropertyFormatting.Format(modByKey[key], modAsset);
            if (oldVal != newVal) changes.Add(new PropChange(KeyLabel(key), oldVal, newVal));
        }
        foreach (var key in modByKey.Keys.Except(baseByKey.Keys))
            changes.Add(new PropChange(KeyLabel(key), "<absent>", PropertyFormatting.Format(modByKey[key], modAsset)));
        foreach (var key in baseByKey.Keys.Except(modByKey.Keys))
            changes.Add(new PropChange(KeyLabel(key), PropertyFormatting.Format(baseByKey[key], baseAsset), "<absent>"));

        return changes;
    }

    static string KeyLabel((string Name, int ArrayIndex) key) => key.ArrayIndex == 0 ? key.Name : $"{key.Name}[{key.ArrayIndex}]";

    static bool FilesIdentical(string a, string b)
    {
        var aExists = File.Exists(a);
        var bExists = File.Exists(b);
        if (aExists != bExists) return false;
        if (!aExists) return true;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    static string ResolveToDir(string path, string label, Action<string>? log)
    {
        if (Directory.Exists(path)) return path;
        if (File.Exists(path) && path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            var dest = Path.Combine(Path.GetTempPath(), $"absolvermodtool_diff_{label}_{Path.GetFileNameWithoutExtension(path)}");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            log?.Invoke($"extracting {path} -> {dest}");
            UnrealPak.Run($"\"{Path.GetFullPath(path)}\" -Extract \"{dest}\"");
            return dest;
        }
        throw new FileNotFoundException($"Not a directory or .pak file: {path}");
    }
}
