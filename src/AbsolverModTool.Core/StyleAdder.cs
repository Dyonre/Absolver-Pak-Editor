using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace AbsolverModTool.Core;

/// <summary>
/// Adds a brand-new fighting style (a seventh <c>UFightingStyleDB</c>) instead of rewriting an existing one:
/// clones a style asset under a new package path, then appends it to <c>DefenseComponent.m_FightingStyles</c>
/// in each character blueprint, creating the two imports (package + object) that reference needs.
/// The new style's id is its index in that array (6 for the first added style), which is what the save's
/// <c>m_uiCombatStyle</c> holds. See docs/disasm/defense-parry-style-findings.md.
/// </summary>
public static class StyleAdder
{
    public static readonly string[] CharacterBlueprints = { "BP_TPSCharacter", "BP_MenuCharacter", "BP_StartUpCharacter" };

    /// <summary>Writes the cloned style asset (with <paramref name="edits"/> applied as property=value) to
    /// <c>outDir</c> at its Content-relative path, and returns that file's path.</summary>
    public static string CloneStyle(string srcDir, string cloneOf, string newName, string outDir,
        IEnumerable<(string Prop, string Value)> edits, Action<string> log)
    {
        var srcPath = AssetAccess.FindAssetFile(srcDir, cloneOf) ?? throw new FileNotFoundException($"style asset '{cloneOf}' not found under {srcDir}");
        var asset = AssetAccess.Load(srcPath);
        var export = asset.Exports.OfType<NormalExport>().First();
        AnimGraft.Rename(asset, export, $"/Game/DB/FightingStyles/{newName}", log);
        foreach (var (prop, value) in edits)
        {
            var fields = AssetAccess.GetEditableFields(asset, null);
            var p = AssetAccess.ResolvePath(fields, prop) ?? throw new InvalidOperationException($"style property '{prop}' not found");
            RecipeEngine.SetValue(p, value, asset);
            log($"set {prop} = {value}");
        }
        var rel = Path.Combine("Absolver", "Content", "DB", "FightingStyles", newName + ".uasset");
        var dest = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        asset.Write(dest);
        log($"wrote {dest}");
        return dest;
    }

    /// <summary>Appends the new style to the blueprint's DefenseComponent style list. <paramref name="templateStyle"/>
    /// names an import already in the list (e.g. "JKD"); its package/class import entries are copied for the new one.
    /// Returns the new element's index in the list (= the style id).</summary>
    public static int AddToBlueprint(string srcDir, string blueprint, string templateStyle, string newName, string outDir, Action<string> log)
    {
        var srcPath = AssetAccess.FindAssetFile(srcDir, blueprint) ?? throw new FileNotFoundException($"blueprint '{blueprint}' not found under {srcDir}");
        var asset = AssetAccess.Load(srcPath);

        int objIdx = asset.Imports.FindIndex(i => i.ObjectName.ToString() == templateStyle && i.OuterIndex.Index < 0);
        if (objIdx < 0) throw new InvalidOperationException($"{blueprint}: no import named '{templateStyle}'");
        var objImport = asset.Imports[objIdx];
        var pkgImport = asset.Imports[-objImport.OuterIndex.Index - 1];
        log($"{blueprint}: template import {objImport.ObjectName} (class {objImport.ClassPackage}.{objImport.ClassName}) in package {pkgImport.ObjectName}");

        // New package import, then the object import that points at it.
        var newPkg = new Import(pkgImport.ClassPackage, pkgImport.ClassName, pkgImport.OuterIndex,
            FName.FromString(asset, $"/Game/DB/FightingStyles/{newName}"), pkgImport.bImportOptional);
        asset.Imports.Add(newPkg);
        var newPkgRef = FPackageIndex.FromRawIndex(-asset.Imports.Count);

        var newObj = new Import(objImport.ClassPackage, objImport.ClassName, newPkgRef,
            FName.FromString(asset, newName), objImport.bImportOptional);
        asset.Imports.Add(newObj);
        var newObjRef = FPackageIndex.FromRawIndex(-asset.Imports.Count);

        // The list lives on the component template export named DefenseComponent.
        var comp = asset.Exports.OfType<NormalExport>().FirstOrDefault(e =>
                e.ObjectName.ToString() == "DefenseComponent" && e.Data.Any(d => d.Name.ToString() == "m_FightingStyles"))
            ?? throw new InvalidOperationException($"{blueprint}: no DefenseComponent export with m_FightingStyles");
        var list = comp.Data.OfType<ArrayPropertyData>().First(d => d.Name.ToString() == "m_FightingStyles");
        var clone = (ObjectPropertyData)list.Value[^1].Clone();
        clone.Value = newObjRef;
        list.Value = list.Value.Append(clone).ToArray();
        log($"{blueprint}: m_FightingStyles now {list.Value.Length} entries, new style id {list.Value.Length - 1}");

        var rel = Path.GetRelativePath(srcDir, srcPath);
        var dest = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        asset.Write(dest);
        return list.Value.Length - 1;
    }
}

public static class QuadrantAnimAdder
{
    /// <summary>Stance-change animations are looked up by style first
    /// (<c>UChangeQuadrantDB::GetAnimation</c>: <c>m_ChangeQuadrantAnims.m_Array[style][weapon][start][end]</c>), so a new
    /// style id needs its own entry or the player cannot change stance in combat. Appends a copy of
    /// <paramref name="cloneFromStyle"/>'s block as the next style id.</summary>
    public static int AddStyleBlock(string srcDir, string outDir, int cloneFromStyle, Action<string> log)
    {
        var srcPath = AssetAccess.FindAssetFile(srcDir, "ChangeQuadrantDB") ?? throw new FileNotFoundException("ChangeQuadrantDB not found");
        var asset = AssetAccess.Load(srcPath);
        var export = asset.Exports.OfType<NormalExport>().First();
        var top = export.Data.OfType<StructPropertyData>().First(p => p.Name.ToString() == "m_ChangeQuadrantAnims");
        var styles = top.Value.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "m_Array");
        log($"ChangeQuadrantDB: {styles.Value.Length} style blocks");
        var clone = (PropertyData)styles.Value[cloneFromStyle].Clone();
        styles.Value = styles.Value.Append(clone).ToArray();
        log($"appended copy of style {cloneFromStyle} as style {styles.Value.Length - 1}");
        var rel = Path.GetRelativePath(srcDir, srcPath);
        var dest = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        asset.Write(dest);
        return styles.Value.Length - 1;
    }
}

public static class StyleTableAdder
{
    /// <summary>Several combat DBs (GuardDB, HittedDB, CancelDB, ...) hold per-style animation blocks as
    /// <c>Prop = struct { m_Array = [style0, style1, ...] }</c>. A style id past the end has no entry, so this appends
    /// a copy of <paramref name="cloneFrom"/>'s block for every named property of one asset.</summary>
    public static void AddBlocks(string srcDir, string outDir, string assetName, int cloneFrom, IEnumerable<string> props, Action<string> log)
    {
        var srcPath = AssetAccess.FindAssetFile(srcDir, assetName) ?? throw new FileNotFoundException($"{assetName} not found under {srcDir}");
        var asset = AssetAccess.Load(srcPath);
        var export = asset.Exports.OfType<NormalExport>().First();
        foreach (var propName in props)
        {
            var top = export.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == propName)
                ?? throw new InvalidOperationException($"{assetName}: struct property '{propName}' not found");
            // Descend through single-field wrapper structs until the first array (the style list).
            static ArrayPropertyData? FirstArray(StructPropertyData sp) =>
                sp.Value.OfType<ArrayPropertyData>().FirstOrDefault()
                ?? sp.Value.OfType<StructPropertyData>().Select(FirstArray).FirstOrDefault(a => a != null);
            var styles = FirstArray(top) ?? throw new InvalidOperationException($"{assetName}.{propName}: no array inside");
            if (styles.Value.Length != 6) throw new InvalidOperationException($"{assetName}.{propName}: outer array has {styles.Value.Length} entries, expected 6 style blocks");
            styles.Value = styles.Value.Append((PropertyData)styles.Value[cloneFrom].Clone()).ToArray();
            log($"{assetName}.{propName}: style blocks 6 -> {styles.Value.Length} (copy of style {cloneFrom})");
        }
        var dest = Path.Combine(outDir, Path.GetRelativePath(srcDir, srcPath));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        asset.Write(dest);
    }

    /// <summary>Like <see cref="AddBlocks"/>, but for tables where the style TArray sits under a fixed-size outer dimension
    /// (DodgeDB.m_Anims[weightCategory].m_array[style], WeaponActionDB.m_LockMoveAnims[action].m_Array[style]): extends EVERY
    /// outermost TArray (the walk does not descend into arrays) that still has exactly 6 style blocks with a copy of block
    /// <paramref name="cloneFrom"/>. Arrays already at 7 are left alone, so it also repairs a table where only the first one was
    /// extended. Returns the number of arrays extended.</summary>
    public static int AddBlocksAll(string srcDir, string outDir, string assetName, int cloneFrom, IEnumerable<string> props, Action<string> log)
    {
        var srcPath = AssetAccess.FindAssetFile(srcDir, assetName) ?? throw new FileNotFoundException($"{assetName} not found under {srcDir}");
        var asset = AssetAccess.Load(srcPath);
        var export = asset.Exports.OfType<NormalExport>().First();
        int total = 0;
        foreach (var propName in props)
        {
            var top = export.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == propName)
                ?? throw new InvalidOperationException($"{assetName}: struct property '{propName}' not found");
            var arrays = new List<ArrayPropertyData>();
            void Walk(PropertyData p)
            {
                if (p is StructPropertyData sp) foreach (var c in sp.Value) Walk(c);
                else if (p is ArrayPropertyData ap) arrays.Add(ap);   // outermost only: do not descend
            }
            Walk(top);
            if (arrays.Count == 0) throw new InvalidOperationException($"{assetName}.{propName}: no array inside");
            int done = 0;
            foreach (var a in arrays)
            {
                if (a.Value.Length == 7) { log($"{assetName}.{propName}: an array already has 7 style blocks, left alone"); continue; }
                if (a.Value.Length != 6) throw new InvalidOperationException($"{assetName}.{propName}: outermost array has {a.Value.Length} entries, expected 6 style blocks");
                a.Value = a.Value.Append((PropertyData)a.Value[cloneFrom].Clone()).ToArray();
                done++;
            }
            log($"{assetName}.{propName}: extended {done} of {arrays.Count} outermost style arrays 6 -> 7 (copy of style {cloneFrom})");
            total += done;
        }
        var dest = Path.Combine(outDir, Path.GetRelativePath(srcDir, srcPath));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        asset.Write(dest);
        return total;
    }

    /// <summary>GuardDB keeps the style as the INNERMOST dimension ([weapon][quadrant][style], 6 animation refs per leaf).
    /// Appends a copy of leaf element <paramref name="cloneFrom"/> to every 6-element object array under the named properties.</summary>
    public static void AddLeaves(string srcDir, string outDir, string assetName, int cloneFrom, IEnumerable<string> props, Action<string> log)
    {
        var srcPath = AssetAccess.FindAssetFile(srcDir, assetName) ?? throw new FileNotFoundException($"{assetName} not found under {srcDir}");
        var asset = AssetAccess.Load(srcPath);
        var export = asset.Exports.OfType<NormalExport>().First();
        foreach (var propName in props)
        {
            var top = export.Data.FirstOrDefault(p => p.Name.ToString() == propName) ?? throw new InvalidOperationException($"{assetName}: '{propName}' not found");
            int leaves = 0, skipped = 0;
            void Walk(PropertyData p)
            {
                if (p is StructPropertyData sp) { foreach (var c in sp.Value) Walk(c); }
                else if (p is ArrayPropertyData ap)
                {
                    if (ap.Value.Length > 0 && ap.Value.All(e => e is ObjectPropertyData))
                    {
                        if (ap.Value.Length == 6) { ap.Value = ap.Value.Append((PropertyData)ap.Value[cloneFrom].Clone()).ToArray(); leaves++; }
                        else skipped++;
                    }
                    else foreach (var c in ap.Value) Walk(c);
                }
            }
            Walk(top);
            log($"{assetName}.{propName}: extended {leaves} leaf arrays (skipped {skipped} of other length)");
        }
        var dest = Path.Combine(outDir, Path.GetRelativePath(srcDir, srcPath));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        asset.Write(dest);
    }
}

/// <summary>Points the style-6 leaf of GuardDB's animation tables at newly cooked animations
/// (<c>m_GuardAnimations[weapon][quadrant][style]</c> idle guard, <c>m_GuardPrepAnimations[weapon][quadrant][height][style]</c>
/// prep-to-block). Quadrant order FL, FR, BR, BL; height order High, Mid, Low; weapons 0 and 2 are the bare-hands/gloves sets.</summary>
public static class GuardWirer
{
    static readonly string[] Quadrants = { "FL", "FR", "BR", "BL" };
    static readonly string[] Heights = { "High", "Mid", "Low" };

    // GuardDB's outer levels are fixed C arrays: a struct holding N same-named struct fields, not a TArray.
    static PropertyData Sub(PropertyData p, int i) =>
        p is StructPropertyData sp ? sp.Value[i] : throw new InvalidOperationException($"expected struct, got {p.GetType().Name}");
    static ArrayPropertyData Leaf(PropertyData p) =>
        p is StructPropertyData sp ? sp.Value.OfType<ArrayPropertyData>().First() : throw new InvalidOperationException($"expected struct, got {p.GetType().Name}");

    /// <summary>Appends two imports (package + AnimSequence object) modelled on an existing animation import and returns the object ref.</summary>
    static FPackageIndex AddAnimImport(UAsset asset, string templateObject, string packagePath, string objectName)
    {
        var tmpl = asset.Imports.First(i => i.ObjectName.ToString() == templateObject && i.OuterIndex.Index < 0);
        var tmplPkg = asset.Imports[-tmpl.OuterIndex.Index - 1];
        asset.Imports.Add(new Import(tmplPkg.ClassPackage, tmplPkg.ClassName, tmplPkg.OuterIndex, FName.FromString(asset, packagePath), tmplPkg.bImportOptional));
        var pkgRef = FPackageIndex.FromRawIndex(-asset.Imports.Count);
        asset.Imports.Add(new Import(tmpl.ClassPackage, tmpl.ClassName, pkgRef, FName.FromString(asset, objectName), tmpl.bImportOptional));
        return FPackageIndex.FromRawIndex(-asset.Imports.Count);
    }

    public static void Wire(string workDir, string animPackageDir, string namePrefix, Action<string> log)
    {
        var path = AssetAccess.FindAssetFile(workDir, "GuardDB") ?? throw new FileNotFoundException("GuardDB not in work dir (run add-style-leaf first)");
        var asset = AssetAccess.Load(path);
        var export = asset.Exports.OfType<NormalExport>().First();
        var guard = export.Data.OfType<StructPropertyData>().First(p => p.Name.ToString() == "m_GuardAnimations");
        var prep = export.Data.OfType<StructPropertyData>().First(p => p.Name.ToString() == "m_GuardPrepAnimations");
        int n = 0;
        foreach (int w in new[] { 0, 2 })
        {
            var gq = Sub(guard, w);
            var pq = Sub(prep, w);
            for (int q = 0; q < 4; q++)
            {
                var leaves = Leaf(Sub(gq, q));
                if (leaves.Value.Length != 7) throw new InvalidOperationException("GuardDB leaf not extended to 7 - run add-style-leaf first");
                var name = $"{namePrefix}_Idle_{Quadrants[q]}";
                ((ObjectPropertyData)leaves.Value[6]).Value = AddAnimImport(asset, "FL_Idle_Guard_KungFu", $"{animPackageDir}/{name}", name);
                n++;
                var hs = Sub(pq, q);
                for (int h = 0; h < 3; h++)
                {
                    var pl = Leaf(Sub(hs, h));
                    var pn = $"{namePrefix}_Prep{Heights[h]}_{Quadrants[q]}";
                    ((ObjectPropertyData)pl.Value[6]).Value = AddAnimImport(asset, "FL_Idle_Guard_KungFu", $"{animPackageDir}/{pn}", pn);
                    n++;
                }
            }
        }
        asset.Write(path);
        log($"GuardDB: wired {n} style-6 animation slots to {namePrefix}_* under {animPackageDir}");
    }
}

/// <summary>Cooked plain animations (guard clips and the like) come out of the UE import without
/// <c>bEnableRootMotion</c>, while Absolver's own guard clips have it on; without it the root bone's rotation stays in
/// the pose and a back-stance guard twists the torso while walking. Attack grafts inherit the flag from their donor.</summary>
public static class RootMotionFixer
{
    public static void Enable(string file, Action<string> log)
    {
        var asset = AssetAccess.Load(file);
        var export = asset.Exports.OfType<NormalExport>().First();
        if (export.Data.Any(p => p.Name.ToString() == "bEnableRootMotion")) { log($"{Path.GetFileName(file)}: already set"); return; }
        asset.AddNameReference(new UAssetAPI.UnrealTypes.FString("BoolProperty"));   // the type name must exist before serialization
        export.Data.Add(new BoolPropertyData(FName.FromString(asset, "bEnableRootMotion")) { Value = true });
        asset.Write(file);
        log($"{Path.GetFileName(file)}: bEnableRootMotion = True");
    }
}

/// <summary>Rewrites a parry-window notify's <c>m_ParryTypeArray</c> so the window accepts every direction/side/height
/// (Sifu's deflect does not care where a hit comes from), instead of the 2 types a vanilla Absolver parry animation carries.</summary>
public static class ParryTypeFixer
{
    public static void AllTypes(string file, Action<string> log)
    {
        var asset = AssetAccess.Load(file);
        var notify = asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.GetExportClassType().ToString().Contains("ParryWindow"))
            ?? throw new InvalidOperationException($"{Path.GetFileName(file)}: no ParryWindow notify");
        var arr = notify.Data.OfType<ArrayPropertyData>().First(p => p.Name.ToString() == "m_ParryTypeArray");
        var template = (StructPropertyData)arr.Value[0];
        var types = new List<PropertyData>();
        foreach (var dir in new[] { "Front", "Back" })
            foreach (var side in new[] { "Left", "Right" })
                foreach (var h in new[] { "High", "Low" })
                {
                    var e = (StructPropertyData)template.Clone();
                    foreach (var f in e.Value.OfType<EnumPropertyData>())
                    {
                        var n = f.Name.ToString();
                        if (n == "m_eParryDirection") f.Value = FName.FromString(asset, $"EParryDirection::{dir}");
                        else if (n == "m_eParrySide") f.Value = FName.FromString(asset, $"EParrySide::{side}");
                        else if (n == "m_eParryHeight") f.Value = FName.FromString(asset, $"EParryHeight::{h}");
                    }
                    types.Add(e);
                }
        arr.Value = types.ToArray();
        asset.Write(file);
        log($"{Path.GetFileName(file)}: parry window now accepts {types.Count} types");
    }
}
