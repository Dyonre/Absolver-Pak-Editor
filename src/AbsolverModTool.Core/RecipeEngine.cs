using System.Globalization;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace AbsolverModTool.Core;

public record ApplyStep(string Asset, string Op, string Message);

public static class RecipeBuilder
{
    public static void AddSetProp(Recipe recipe, string asset, string row, string property, string value) =>
        recipe.Edits.Add(new Edit { Op = "set-prop", Asset = asset, Row = row, Property = property, Value = value });

    public static void AddCloneRow(Recipe recipe, string asset, string sourceRow, string newRow) =>
        recipe.Edits.Add(new Edit { Op = "clone-row", Asset = asset, SourceRow = sourceRow, NewRow = newRow });

    public static void AddArrayElement(Recipe recipe, string asset, string row, string property, string value) =>
        recipe.Edits.Add(new Edit { Op = "add-array-element", Asset = asset, Row = row, Property = property, Value = value });

    /// <summary>Marks an attacks row so that at pack time its animation's baked notifies are retimed to the row's final timeline
    /// (<see cref="AnimRetime"/>). One per row; adding it again is a no-op.</summary>
    public static bool AddRetimeAnim(Recipe recipe, string asset, string row)
    {
        if (recipe.Edits.Any(e => e.Op == "retime-anim" && e.Asset == asset && e.Row == row)) return false;
        recipe.Edits.Add(new Edit { Op = "retime-anim", Asset = asset, Row = row });
        return true;
    }

    public static bool RemoveRetimeAnim(Recipe recipe, string asset, string row)
    {
        var e = recipe.Edits.FirstOrDefault(x => x.Op == "retime-anim" && x.Asset == asset && x.Row == row);
        return e != null && recipe.Edits.Remove(e);
    }
}

public static class RecipeEngine
{
    /// <summary>
    /// Applies every edit in the recipe against fresh copies from <paramref name="srcDir"/>,
    /// writing the results to <paramref name="outDir"/>. Never touches <paramref name="srcDir"/>.
    /// Throws on the first bad edit - callers (CLI/GUI) decide how to report that.
    /// </summary>
    public static List<ApplyStep> Apply(string recipePath, string outDir, string srcDir)
    {
        if (!File.Exists(recipePath)) throw new FileNotFoundException($"Recipe not found: {recipePath}");
        if (!Directory.Exists(srcDir)) throw new DirectoryNotFoundException($"Source directory not found: {srcDir}");

        var recipe = RecipeIO.Load(recipePath);
        if (recipe.Edits.Count == 0) throw new InvalidOperationException("Recipe has no edits.");

        var results = new List<ApplyStep>();

        foreach (var group in recipe.Edits.GroupBy(e => e.Asset))
        {
            var assetName = group.Key;
            var srcPath = AssetAccess.FindAssetFile(srcDir, assetName)
                ?? throw new FileNotFoundException($"No '{assetName}.uasset' found under {srcDir}");

            var asset = AssetAccess.Load(srcPath);
            var table = AssetAccess.GetTable(asset);

            foreach (var edit in group)
            {
                switch (edit.Op)
                {
                    case "clone-row":
                        ApplyCloneRow(asset, table, edit);
                        break;
                    case "set-prop":
                        ApplySetProp(asset, edit);
                        break;
                    case "add-array-element":
                        ApplyAddArrayElement(asset, edit);
                        break;
                    case "retime-anim":
                        // Needs the row's FINAL values, so it runs after every other edit to this asset (below).
                        continue;
                    default:
                        throw new NotSupportedException($"unknown op '{edit.Op}'");
                }
                results.Add(new ApplyStep(assetName, edit.Op, "applied"));
            }

            foreach (var rt in group.Where(e => e.Op == "retime-anim"))
                results.Add(ApplyRetimeAnim(asset, table, rt, srcDir, outDir));

            var rel = Path.GetRelativePath(srcDir, srcPath);
            var destAsset = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destAsset)!);
            asset.Write(destAsset);
            results.Add(new ApplyStep(assetName, "write", rel));
        }

        return results;
    }

    /// <summary>Retimes the notifies of the animation referenced by <paramref name="edit"/>'s attacks row to that row's (already edited)
    /// timeline and writes the animation next to the other output assets. Problems (animation not in the source folder, e.g. it ships in a
    /// mod pak; a different number of strikes) are reported in the returned step, not thrown, so one such row doesn't block the whole pack.</summary>
    public static ApplyStep ApplyRetimeAnim(UAsset asset, DataTableExport? table, Edit edit, string srcDir, string outDir)
    {
        string label = $"{edit.Asset}[{edit.Row}]";
        if (table == null || edit.Row == null) return new ApplyStep(edit.Asset, edit.Op, $"{label}: skipped (needs a DataTable row)");
        var fields = AssetAccess.GetEditableFields(asset, edit.Row);
        var anim = fields.OfType<SoftObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == "m_Anim")?.Value.AssetPath.AssetName?.ToString();
        if (string.IsNullOrEmpty(anim) || anim == "None") return new ApplyStep(edit.Asset, edit.Op, $"{label}: skipped (row has no m_Anim)");
        var file = AnimRetime.FindByGamePath(srcDir, anim);
        if (file == null) return new ApplyStep(edit.Asset, edit.Op, $"{label}: skipped - {anim} is not under {srcDir} (an animation that ships in a mod pak can't be retimed from here)");

        var shared = table.Table.Data.OfType<StructPropertyData>()
            .Where(r => r.Name.ToString() != edit.Row && r.Value.OfType<SoftObjectPropertyData>().Any(p => p.Name.ToString() == "m_Anim" && p.Value.AssetPath.AssetName?.ToString() == anim))
            .Select(r => r.Name.ToString()).ToList();

        // The row this animation was authored for = the vanilla row that references it (the edited row may be a clone, or already edited).
        var vanillaAttacks = AssetAccess.FindAssetFile(srcDir, edit.Asset);
        AnimRetime.Timing? original = null;
        if (vanillaAttacks != null)
        {
            var van = AssetAccess.Load(vanillaAttacks); var vt = AssetAccess.GetTable(van);
            var owner = vt?.Table.Data.OfType<StructPropertyData>().FirstOrDefault(r => r.Value.OfType<SoftObjectPropertyData>().Any(p => p.Name.ToString() == "m_Anim" && p.Value.AssetPath.AssetName?.ToString() == anim));
            if (owner != null) original = AnimRetime.ReadTiming(owner.Value);
        }
        if (original == null) return new ApplyStep(edit.Asset, edit.Op, $"{label}: skipped - no vanilla row uses {anim}, so there is no original timing to measure the edit against");

        var notes = new List<string>();
        var res = AnimRetime.RetimeFile(file, AnimRetime.ReadTiming(fields), original, Path.Combine(outDir, Path.GetRelativePath(srcDir, file)), m => notes.Add(m));
        if (!res.Changed) return new ApplyStep(edit.Asset, edit.Op, $"{label}: skipped - {res.Problem}");
        var msg = $"{label}: notifies of {Path.GetFileName(file)} retimed to the row ({res.RowEndSeconds * 30:0}f row, {res.ClipSeconds * 30:0}f clip)";
        if (shared.Count > 0) msg += $"; WARNING the same animation is also used by row(s) {string.Join(", ", shared)}, which get the new timing too";
        msg += string.Concat(notes.Where(n => n.StartsWith("WARNING")).Select(n => "; " + n));
        return new ApplyStep(edit.Asset, edit.Op, msg);
    }

    public static void ApplyCloneRow(UAsset asset, DataTableExport? table, Edit edit)
    {
        if (table == null) throw new InvalidOperationException($"{edit.Asset} is not a DataTable; clone-row doesn't apply");
        if (edit.SourceRow == null || edit.NewRow == null) throw new InvalidOperationException("clone-row edit missing sourceRow/newRow");

        var src = table[edit.SourceRow] as StructPropertyData
            ?? throw new InvalidOperationException($"source row '{edit.SourceRow}' not found in {edit.Asset}");

        var clone = (StructPropertyData)src.Clone();
        clone.Name = FName.FromString(asset, edit.NewRow);

        var nameField = clone.Value.FirstOrDefault(p => p.Name.ToString() == "m_Name") as NamePropertyData;
        if (nameField != null) nameField.Value = FName.FromString(asset, edit.NewRow);

        table.Table.Data.Add(clone);
    }

    public static void ApplySetProp(UAsset asset, Edit edit)
    {
        if (edit.Row == null && AssetAccess.GetTable(asset) != null)
            throw new InvalidOperationException("set-prop edit missing row");
        if (edit.Property == null || edit.Value == null)
            throw new InvalidOperationException("set-prop edit missing property/value");

        var fields = AssetAccess.GetEditableFields(asset, edit.Row);
        var prop = AssetAccess.ResolvePath(fields, edit.Property);
        if (prop == null)
        {
            var names = string.Join(", ", AssetAccess.FlattenForEditing(fields).Select(f => f.Path).Distinct().OrderBy(n => n));
            throw new InvalidOperationException($"property '{edit.Property}' not found. Available: {names}");
        }

        SetValue(prop, edit.Value, asset);
    }

    /// <summary>
    /// Appends a new element to a true growable array property (e.g. a combat deck's quadrant
    /// chain), so a slot that isn't populated in this particular file can be added rather than
    /// only edited. The new element is a clone of the array's last element (so it carries the
    /// right tag metadata for its type) with its value immediately set - there's no way to
    /// construct a correctly-tagged element from nothing, so a genuinely empty array (no
    /// elements at all to use as a template) can't be appended to yet.
    /// </summary>
    public static void ApplyAddArrayElement(UAsset asset, Edit edit)
    {
        if (edit.Property == null || edit.Value == null)
            throw new InvalidOperationException("add-array-element edit missing property/value");

        var fields = AssetAccess.GetEditableFields(asset, edit.Row);
        var arrayProp = fields.FirstOrDefault(p => p.Name.ToString() == edit.Property) as ArrayPropertyData
            ?? throw new InvalidOperationException($"'{edit.Property}' is not an array property (or not found) in {edit.Asset}");

        if (arrayProp.Value.Length == 0)
            throw new InvalidOperationException($"'{edit.Property}' has no existing elements to use as a template - can't append to a genuinely empty array yet");

        var newElement = (PropertyData)arrayProp.Value[^1].Clone();
        SetValue(newElement, edit.Value, asset);
        arrayProp.Value = arrayProp.Value.Append(newElement).ToArray();
    }

    /// <summary>Names of the growable array properties on a row/object - what a caller can offer
    /// to append to via <see cref="AddArrayElement"/>/<see cref="ApplyAddArrayElement"/>.</summary>
    public static List<(string Name, int Count)> GetArrayProperties(List<PropertyData> fields) =>
        fields.OfType<ArrayPropertyData>().Select(a => (a.Name.ToString(), a.Value.Length)).ToList();

    /// <summary>Replays every edit for one asset (in recipe order) onto an already-loaded copy of
    /// that asset. Used by the GUI to rebuild a fresh in-memory view - after an edit is removed,
    /// or to pick up a rename/clone for the attack-name lookup - without waiting for a full
    /// Finalize round-trip. Unlike <see cref="Apply"/>, this only touches the one asset already
    /// passed in and never writes anything to disk.
    ///
    /// Each edit is applied independently (a failure is reported via <paramref name="onError"/>
    /// and that one edit is skipped, not thrown) - this is a best-effort live preview, not the
    /// real build, so one edit left dangling (e.g. a rename that still targets a row whose own
    /// clone-row edit was since removed) shouldn't also wipe out every other, unrelated edit that
    /// happens to be replayed in the same pass. <see cref="Apply"/> (the actual Finalize/pack path)
    /// deliberately keeps failing hard on the first bad edit instead - a genuinely broken recipe
    /// should never silently ship.</summary>
    public static void ReplayEdits(UAsset asset, DataTableExport? table, string assetName, IEnumerable<Edit> edits, Action<Edit, Exception>? onError = null)
    {
        foreach (var edit in edits.Where(ed => ed.Asset == assetName))
        {
            try
            {
                switch (edit.Op)
                {
                    case "clone-row": ApplyCloneRow(asset, table, edit); break;
                    case "set-prop": ApplySetProp(asset, edit); break;
                    case "add-array-element": ApplyAddArrayElement(asset, edit); break;
                    // retime-anim only matters at pack time (it rewrites a different asset)
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(edit, ex);
            }
        }
    }

    /// <summary>Whether SetValue can handle this property's concrete type - the single source
    /// of truth for what a caller (CLI or GUI) should even attempt to let the user edit.</summary>
    public static bool IsSupported(PropertyData p) => p switch
    {
        BoolPropertyData => true,
        IntPropertyData => true,
        FloatPropertyData => true,
        BytePropertyData byp => byp.ByteType == BytePropertyType.Byte,
        EnumPropertyData => true,
        NamePropertyData => true,
        SoftObjectPropertyData => true,
        TextPropertyData => true,
        _ => false,
    };

    public static void SetValue(PropertyData p, string value, UAsset asset)
    {
        switch (p)
        {
            case BoolPropertyData bp:
                bp.Value = value is "1" or "true" or "True" or "TRUE";
                break;
            case IntPropertyData ip:
                ip.Value = int.Parse(value, CultureInfo.InvariantCulture);
                break;
            case FloatPropertyData fp:
                fp.Value = float.Parse(value, CultureInfo.InvariantCulture);
                break;
            case BytePropertyData byp when byp.ByteType == BytePropertyType.Byte:
                byp.Value = byte.Parse(value, CultureInfo.InvariantCulture);
                break;
            case EnumPropertyData ep:
                // The table stores enum values fully qualified ("EQuadrantTypes::FrontLeft"). Keep that
                // form: accept either spelling, and for a bare value borrow the prefix from the value
                // being replaced. (This used to strip the prefix instead, writing "FrontLeft" - a name
                // the game's enum doesn't have. Found 2026-10-02 when the first enum edits were dumped
                // back and didn't look like their vanilla neighbours.)
                var current = ep.Value?.ToString() ?? "";
                var prefix = current.Contains("::") ? current.Split("::")[0] + "::" : "";
                ep.Value = FName.FromString(asset, value.Contains("::") ? value : prefix + value);
                break;
            case NamePropertyData np:
                np.Value = FName.FromString(asset, value);
                break;
            case SoftObjectPropertyData sop:
                // This game's data stores the whole "/Game/Path/Asset.Asset" string in
                // AssetName, leaving PackageName and SubPathString empty - confirmed by
                // inspecting existing values (e.g. m_Anim) and reusing those already-valid
                // empty fields rather than constructing new ones from scratch.
                var newAssetName = FName.FromString(asset, value);
                sop.Value = new FSoftObjectPath(new FTopLevelAssetPath(sop.Value.AssetPath.PackageName, newAssetName), sop.Value.SubPathString);
                break;
            case TextPropertyData tp:
                // Renames the displayed text (e.g. an attack's real name) without touching the
                // localization key/namespace - confirmed by inspecting an existing value:
                // CultureInvariantString holds the actual display string ("Plexus Elbow"), not
                // the key ("ATTACK_REALNAME_212"). Round-trip proven safe.
                tp.CultureInvariantString = new FString(value);
                // The key has to change with the text. The game resolves display text per
                // namespace+key, so two rows that share a key show the same name no matter what
                // their own strings say - seen in-game 2026-10-02: two rows cloned from Outward Kick
                // (key ATTACK_REALNAME_195) both displayed "High Roundhouse Kick". A key the game's
                // localization table doesn't know falls back to the string stored here, which is
                // what a renamed or new row needs. Key = old prefix + the new text, so distinct
                // names always get distinct keys.
                if (tp.Value?.Value is { Length: > 0 } oldKey)
                {
                    var keyPrefix = oldKey.Contains('_') ? oldKey[..(oldKey.LastIndexOf('_') + 1)] : oldKey + "_";
                    tp.Value = new FString(keyPrefix + new string(value.Where(char.IsLetterOrDigit).ToArray()));
                }
                break;
            default:
                throw new NotSupportedException($"set-prop doesn't support property type {p.GetType().Name} yet");
        }
    }
}
