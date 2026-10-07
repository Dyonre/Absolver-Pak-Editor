using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace AbsolverModTool.Core;

public class RowNotFoundException : Exception
{
    public IReadOnlyList<string> AvailableKeys { get; }

    public RowNotFoundException(string key, IReadOnlyList<string> availableKeys)
        : base($"Row '{key}' not found. Available keys: {string.Join(", ", availableKeys)}")
    {
        AvailableKeys = availableKeys;
    }
}

public static class AssetAccess
{
    /// <summary>Filename (no extension) -&gt; full path, built once per directory the first time
    /// it's searched and reused after that. Needed once <c>dir</c> can be the full ~20k-file game
    /// extraction rather than a small curated fixture set - a plain <c>Directory.GetFiles(dir, ...,
    /// AllDirectories)</c> per lookup took ~36 seconds to resolve one 415-item catalog against that
    /// full extraction (a fresh recursive walk per item); this drops it to about the cost of one
    /// walk total, regardless of how many names get looked up afterwards.</summary>
    static readonly Dictionary<string, Dictionary<string, string>> FileIndexCache = new(StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, string> FileIndex(string dir)
    {
        if (FileIndexCache.TryGetValue(dir, out var cached)) return cached;
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
            foreach (var path in Directory.EnumerateFiles(dir, "*.uasset", SearchOption.AllDirectories))
                index.TryAdd(Path.GetFileNameWithoutExtension(path), path); // first one wins on a genuine name collision
        FileIndexCache[dir] = index;
        return index;
    }

    public static string? FindAssetFile(string dir, string assetName) =>
        FileIndex(dir).TryGetValue(assetName, out var path) ? path : null;

    public static UAsset Load(string path) => new(path, EngineVersion.VER_UE4_18);

    public static DataTableExport? GetTable(UAsset asset) => asset.Exports.OfType<DataTableExport>().FirstOrDefault();

    public static NormalExport? GetNormal(UAsset asset) => asset.Exports.OfType<NormalExport>().FirstOrDefault();

    public static List<string> GetRowKeys(DataTableExport table) =>
        table.Table.Data.Select(r => r.Name.ToString()).OrderBy(n => n).ToList();

    /// <summary>
    /// Best-effort human-readable label for a row whose key is just an opaque id (e.g. attacks
    /// table row "212", shop row "1"). Prefers a localized display TextProperty (e.g. an attack's
    /// "Plexus Elbow"); falls back to a resolved hard-object reference's import/export name (e.g.
    /// a shop row's linked equipment DataAsset). Returns null if neither is found.
    /// </summary>
    public static string? GuessRowLabel(StructPropertyData row, UAsset asset)
    {
        var text = row.Value.OfType<TextPropertyData>().FirstOrDefault();
        if (text != null)
        {
            var formatted = PropertyFormatting.Format(text, asset);
            var last = formatted.Split(',')[^1].Trim();
            if (!string.IsNullOrWhiteSpace(last)) return last;
        }

        var obj = row.Value.OfType<ObjectPropertyData>().FirstOrDefault(o => !o.IsNull());
        if (obj != null)
        {
            var formatted = PropertyFormatting.Format(obj, asset);
            var idx = formatted.IndexOf(':');
            if (idx >= 0) return formatted[(idx + 1)..];
        }

        return null;
    }

    /// <summary>
    /// The field list for a row (DataTable asset) or for the whole object (plain-object asset,
    /// where <paramref name="rowKey"/> is ignored). Throws <see cref="RowNotFoundException"/> with
    /// the available keys when a DataTable row key doesn't match, so callers (CLI or GUI) can show
    /// a helpful message instead of a bare null-reference.
    /// </summary>
    public static List<PropertyData> GetFields(UAsset asset, string? rowKey)
    {
        var table = GetTable(asset);
        if (table != null)
        {
            if (rowKey == null) throw new ArgumentException("A row key is required for a DataTable asset.");
            var row = table[rowKey] as StructPropertyData;
            if (row == null) throw new RowNotFoundException(rowKey, GetRowKeys(table));
            return row.Value;
        }

        var normal = GetNormal(asset)
            ?? throw new InvalidOperationException($"Asset has no DataTable or plain object export ({asset.Exports.FirstOrDefault()?.GetType().Name ?? "no exports"}).");
        return normal.Data;
    }

    /// <summary>
    /// Same as <see cref="GetFields"/>, but if the result is exactly one StructProperty
    /// wrapping everything - as with a combat deck's single "m_Struct" field - transparently
    /// unwraps it so editors see the real fields (the quadrant arrays, the alternate-attack
    /// slots) instead of one opaque wrapper that looks unreadable and can't be edited.
    /// </summary>
    public static List<PropertyData> GetEditableFields(UAsset asset, string? rowKey)
    {
        var fields = GetFields(asset, rowKey);
        if (fields.Count == 1 && fields[0] is StructPropertyData wrapper) return wrapper.Value;
        return fields;
    }

    /// <summary>
    /// Expands a field list for display/editing: a true growable array (ArrayProperty) becomes
    /// one row per element, addressed as "Name[i]"; everything else (including a UE4 static
    /// array's separately-tagged entries, which already carry their own ArrayIndex) becomes one
    /// row using its normal label. The returned path is what <see cref="ResolvePath"/> expects
    /// back when an edit is recorded.
    /// </summary>
    public static List<(string Path, PropertyData Property)> FlattenForEditing(List<PropertyData> fields)
    {
        var result = new List<(string, PropertyData)>();
        foreach (var p in fields)
        {
            if (p is ArrayPropertyData ap)
                for (int i = 0; i < ap.Value.Length; i++)
                    result.Add(($"{p.Name}[{i}]", ap.Value[i]));
            else
                result.Add((PropertyFormatting.Label(p), p));
        }
        return result;
    }

    /// <summary>
    /// Resolves a path like "m_iFragmentPrice" or "m_ComboQuadrantsFrontLeftXls[0]" (from
    /// <see cref="FlattenForEditing"/>) back to the concrete property to read/write. A bracket
    /// suffix means either a UE4 static array's own separately-tagged entry at that ArrayIndex
    /// (checked first, since it's a real top-level field) or an element inside a true growable
    /// array property of that name - whichever actually exists.
    /// </summary>
    public static PropertyData? ResolvePath(List<PropertyData> fields, string path)
    {
        var bracket = path.IndexOf('[');
        if (bracket < 0)
            return fields.FirstOrDefault(p => p.ArrayIndex == 0 && p.Name.ToString() == path);

        var name = path[..bracket];
        var index = int.Parse(path[(bracket + 1)..path.IndexOf(']')]);

        // Excludes ArrayPropertyData itself: a true array's own top-level ArrayIndex is also 0
        // by default, which would otherwise wrongly match here for index 0 before case (b) below
        // ever gets a chance to index into it.
        var direct = fields.FirstOrDefault(p => p.Name.ToString() == name && p.ArrayIndex == index && p is not ArrayPropertyData);
        if (direct != null) return direct;

        if (fields.FirstOrDefault(p => p.Name.ToString() == name && p.ArrayIndex == 0) is ArrayPropertyData arrayField
            && index >= 0 && index < arrayField.Value.Length)
            return arrayField.Value[index];

        return null;
    }

    /// <summary>
    /// Maps every row key in the "attacks" DataTable under <paramref name="dir"/> to its
    /// human-readable name (m_RealAttackName's display text, e.g. "212" -> "Plexus Elbow").
    /// Used to make combat decks (which reference attacks only by opaque row-key strings)
    /// readable. Returns an empty map if there's no attacks table under that root.
    /// </summary>
    public static Dictionary<string, string> BuildAttackNameLookup(string dir)
    {
        var path = FindAssetFile(dir, "attacks");
        if (path == null) return new Dictionary<string, string>();
        return BuildAttackNameLookup(Load(path));
    }

    /// <summary>Same as the directory-based overload, but off an already-loaded attacks asset -
    /// for a caller that has replayed pending recipe edits onto a fresh copy first, so a
    /// just-cloned or just-renamed row shows up immediately instead of only after a save/reload.</summary>
    public static Dictionary<string, string> BuildAttackNameLookup(UAsset attacksAsset)
    {
        var map = new Dictionary<string, string>();
        var table = GetTable(attacksAsset);
        if (table == null) return map;

        foreach (var row in table.Table.Data)
        {
            var label = GuessRowLabel(row, attacksAsset) ?? row.Name.ToString();
            map[row.Name.ToString()] = label;
        }
        return map;
    }

    /// <summary>Everything a combo-deck editor needs to enforce the game's own combo rules for one
    /// move: <see cref="StartQuadrant"/>/<see cref="EndQuadrant"/> are the short enum names
    /// (<c>"FrontLeft"</c> etc., matching <c>AbsolverSaveTool.Core.ComboDeck.Quadrant</c> exactly) a
    /// move begins and ends in - the game only lets a move follow another when the follower's
    /// <see cref="StartQuadrant"/> equals the leader's <see cref="EndQuadrant"/>. The three
    /// <c>With*</c> flags say which weapon categories the move is usable in at all - confirmed
    /// separate, independent flags (a move can be true for more than one).</summary>
    /// <summary><see cref="Startup"/>/<see cref="HitAdv"/>/<see cref="GuardAdv"/>/
    /// <see cref="HitTarget"/>/<see cref="MovementType"/>/<see cref="Side"/> are the
    /// absolver.dev-style frame data (confirmed 2026-09-13 against Jab Punch's own real displayed
    /// stats - STARTUP 10/HIT ADV 4/GUARD ADV 0/HIGH-THRUST/LEFT all matched exactly): direct fields
    /// on the attack row (<c>m_iFrameBuildUp</c>, <c>m_iAdvFrameOnPLHit</c>,
    /// <c>m_iAdvFrameOnPLGuard</c>, <c>m_eHitTarget</c>, <c>m_eMovementType1</c>), no derived-stat
    /// formula needed - <see cref="Side"/> is the one exception, derived from
    /// <c>m_eWeaponSlotAttacking1</c> (e.g. <c>LeftArm</c> -&gt; <c>Left</c>) since the game doesn't
    /// have a plain "side" field of its own.</summary>
    public record MoveInfo(string Id, string Name, string StartQuadrant, string EndQuadrant, bool WithBareHands, bool WithSword, bool WithWarGloves, int XpToUnlock,
        int Startup, int HitAdv, int GuardAdv, string HitTarget, string MovementType, string Side);

    /// <summary>Reads every row of the "attacks" DataTable into a <see cref="MoveInfo"/> lookup, for
    /// a combo-deck editor that wants to enforce (rather than just display) the game's chain and
    /// weapon-category rules. Returns an empty map if there's no attacks table under
    /// <paramref name="dir"/>.</summary>
    public static Dictionary<string, MoveInfo> BuildMoveInfoLookup(string dir)
    {
        var result = new Dictionary<string, MoveInfo>();
        var path = FindAssetFile(dir, "attacks");
        if (path == null) return result;
        var asset = Load(path);
        var table = GetTable(asset);
        if (table == null) return result;

        foreach (var row in table.Table.Data)
        {
            string EnumShort(string name) =>
                (row.Value.FirstOrDefault(p => p.Name.ToString() == name)?.ToString() ?? "").Split("::")[^1];
            bool Flag(string name) => (row.Value.FirstOrDefault(p => p.Name.ToString() == name) as BoolPropertyData)?.Value ?? false;
            int Num(string name) => (row.Value.FirstOrDefault(p => p.Name.ToString() == name) as IntPropertyData)?.Value ?? 0;

            var weaponSlot = EnumShort("m_eWeaponSlotAttacking1");
            var side = weaponSlot.Contains("Left") ? "Left" : weaponSlot.Contains("Right") ? "Right" : weaponSlot == "Both" ? "Both" : "";

            var id = row.Name.ToString();
            result[id] = new MoveInfo(id, GuessRowLabel(row, asset) ?? id,
                EnumShort("m_eStartQuadrant"), EnumShort("m_eEndQuadrant"),
                Flag("m_bWithBareHands"), Flag("m_bWithSword"), Flag("m_bWithWarGloves"),
                Num("m_iXPRequiredToUnlock"),
                Num("m_iFrameBuildUp"), Num("m_iAdvFrameOnPLHit"), Num("m_iAdvFrameOnPLGuard"),
                EnumShort("m_eHitTarget"), EnumShort("m_eMovementType1"), side);
        }
        return result;
    }

    /// <summary>One ownable item's shop listing: <see cref="AssetPath"/> is the full
    /// <c>/Game/.../Name.Name</c> form save files store (reconstructed from the shop row's import
    /// reference - see below), <see cref="Category"/> is the game's own native class name for it
    /// (<c>EquipmentData</c> = cosmetic, <c>VisibleWeaponData</c> = weapon, <c>UsableItemData</c> =
    /// power/item) - reliable, since it comes from the asset's actual type rather than guessed from
    /// its path.</summary>
    /// <summary><see cref="MaterialVariants"/> is every dye/color-variant material index this item
    /// is known to drop with (from a loot list's <c>m_Materials[]</c>) - empty when the item was
    /// only ever seen in a shop listing, which names one specific variant being sold rather than
    /// enumerating all the item's possible variants. An empty list here doesn't necessarily mean
    /// the item can't be dyed at all, just that this catalog has no evidence of more than one
    /// variant for it.</summary>
    /// <summary><see cref="DisplayName"/> and <see cref="Slot"/> come from the item's own
    /// DataAsset file (not the shop/loot-list row) - every item of all three kinds carries
    /// <c>m_ItemName</c> (the real in-game name, e.g. "Ripan Lord Top" for
    /// TearSet_02_Prestige_UnderTop_DataAsset) and <c>m_EquipmentSlot</c> (a finer-grained slot
    /// than <see cref="Category"/> - Belt/Elbow/Gloves/OverTop/Shoes/Shoulder/Trouser/UnderTop/
    /// Mask/Hair/Weapon/Item, etc.). Falls back to the bare asset path / the shop-table
    /// <see cref="Category"/> when the item's own file can't be found or read - this can happen
    /// for a loot-list-only reference whose file isn't under the scanned directory.</summary>
    public record CatalogItem(string AssetPath, string Category, int FragmentPrice, int GMLevelRequired, IReadOnlyList<int> MaterialVariants, string DisplayName, string Slot);

    /// <summary>Every ownable item, weapon, and power in the game: the shop DataTables
    /// (<c>EquipmentShop</c>/<c>WeaponShop</c>/<c>PowersShop</c> - every purchasable item has to be
    /// listed in one of these) merged with the loot-box reward pools (<c>LB_Small_BaseLootList</c>/
    /// <c>LB_Big_MainLootList</c>, referenced by <c>LootBoxDB</c>'s <c>m_SmallLootDataTable</c>/
    /// <c>m_BigLootDataTables</c>) - together a much more complete catalog than the shop alone,
    /// since quest/mini-boss-reward items (Shockwave, Shield, ...) were never sold in a shop but
    /// can still come out of a loot box and do appear in these lists, complete with their known
    /// dye-variant options.
    ///
    /// A row's item reference is an <c>ObjectProperty</c> pointing at an *import* (the referenced
    /// asset lives in another package, not this one) - <see cref="PropertyFormatting.Format"/> only
    /// prints that import's own short object name (e.g. "TearSet_02_Prestige_UnderTop_DataAsset"),
    /// not the full path a save file needs to match against. The full path is one level up the
    /// import's outer chain: the import's own <c>OuterIndex</c> points at a second import whose
    /// <c>ClassName</c> is literally "Package" and whose <c>ObjectName</c> *is* the full package
    /// path - concatenating <c>{outerPackagePath}.{objectShortName}</c> reproduces the exact
    /// <c>SoftObjectPath</c>/<c>ObjectProperty</c> string format used throughout the save file
    /// (confirmed by hand-tracing sample rows from both the shop and loot-list tables).</summary>
    public static List<CatalogItem> BuildItemCatalog(string dir)
    {
        var byPath = new Dictionary<string, (string Category, int Price, int Gm, HashSet<int> Materials)>(StringComparer.OrdinalIgnoreCase);

        (string fullPath, string category)? ResolveImport(ObjectPropertyData prop, UAsset asset)
        {
            if (!prop.IsImport()) return null;
            var import = prop.ToImport(asset);
            if (import.OuterIndex.Index >= 0) return null; // not import-relative - shouldn't happen for these tables, skip defensively
            var outer = asset.Imports[-import.OuterIndex.Index - 1];
            return ($"{outer.ObjectName}.{import.ObjectName}", import.ClassName.ToString());
        }

        void Merge(string fullPath, string category, int price, int gm, IEnumerable<int>? materials)
        {
            if (!byPath.TryGetValue(fullPath, out var entry)) entry = (category, price, gm, new HashSet<int>());
            if (materials != null) foreach (var m in materials) entry.Materials.Add(m);
            if (price > 0 && entry.Price == 0) entry = entry with { Price = price };
            byPath[fullPath] = entry;
        }

        void LoadShop(string tableName, string itemField)
        {
            var path = FindAssetFile(dir, tableName);
            if (path == null) return;
            var asset = Load(path);
            if (GetTable(asset) == null) return;
            foreach (var key in GetRowKeys(GetTable(asset)!))
            {
                var fields = GetFields(asset, key);
                if (fields.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == itemField) is not { } prop) continue;
                if (ResolveImport(prop, asset) is not (string fullPath, string category)) continue;
                int Get(string name) => (fields.FirstOrDefault(p => p.Name.ToString() == name) as IntPropertyData)?.Value ?? 0;
                Merge(fullPath, category, Get("m_iFragmentPrice"), Get("m_iGMLevelRequired"), null);
            }
        }

        void LoadLootList(string tableName)
        {
            var path = FindAssetFile(dir, tableName);
            if (path == null) return;
            var asset = Load(path);
            if (GetTable(asset) == null) return;
            foreach (var key in GetRowKeys(GetTable(asset)!))
            {
                var fields = GetFields(asset, key);
                if (fields.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.ToString() == "m_Item") is not { } prop) continue;
                if (ResolveImport(prop, asset) is not (string fullPath, string category)) continue;
                int gm = (fields.FirstOrDefault(p => p.Name.ToString() == "m_iGMLevelRequired") as IntPropertyData)?.Value ?? 0;
                var materials = (fields.FirstOrDefault(p => p.Name.ToString() == "m_Materials") as ArrayPropertyData)?.Value.OfType<IntPropertyData>().Select(p => p.Value);
                Merge(fullPath, category, 0, gm, materials);
            }
        }

        void LoadItemsDb(string tableName)
        {
            var path = FindAssetFile(dir, tableName);
            if (path == null) return;
            var asset = Load(path);
            var fields = GetFields(asset, null);
            var items = (fields.FirstOrDefault(p => p.Name.ToString() == "m_Items") as ArrayPropertyData)?.Value.OfType<ObjectPropertyData>();
            if (items == null) return;
            foreach (var prop in items)
            {
                if (ResolveImport(prop, asset) is not (string fullPath, string category)) continue;
                Merge(fullPath, category, 0, 0, null);
            }
        }

        LoadShop("EquipmentShop", "m_Equipment");
        LoadShop("WeaponShop", "m_Weapon");
        LoadShop("PowersShop", "m_Power");
        LoadLootList("LB_Small_BaseLootList");
        LoadLootList("LB_Big_MainLootList");
        // ItemsDB (DB/Items/ItemsDB.uasset) is the master list of every power, including the
        // reward-only ones (Earthquake, Exhaust, Gravity, LightHealPower, Shield, Shockwave,
        // Silence) that a mini-boss grants directly via a hardcoded Blueprint event on defeat -
        // they're real DataAssets but were never in PowersShop or a loot list, so without this
        // they never got a catalog entry (and therefore never an icon-export attempt) at all.
        // Confirmed via `dump-row Shield` that these powers' own DataAsset (class UsableItemData)
        // carries no m_ItemName/m_InventoryIcon field the way equipment items do, though - so this
        // fixes their name/entry, not their icon (see docs/asset-format-notes.md).
        LoadItemsDb("ItemsDB");

        return byPath.Select(kv =>
        {
            var (name, slot) = ResolveItemNameAndSlot(dir, kv.Key);
            return new CatalogItem(kv.Key, kv.Value.Category, kv.Value.Price, kv.Value.Gm, kv.Value.Materials.OrderBy(x => x).ToList(), name, slot);
        }).ToList();
    }

    /// <summary>One row of an "unlock by short id" shop table (EmoteShop/IntroShop) - unlike
    /// equipment/weapons/powers, emotes and intros are stored in the save as a bare numeric
    /// <see cref="Id"/> string (e.g. "038"), not an asset path. <see cref="Id"/> comes from the
    /// row's own <c>m_RowName</c> field; <see cref="Name"/> is the row's key (e.g. "Angry"),
    /// the only human-readable name available - there's no separate m_ItemName-style field here.</summary>
    public record NameCatalogItem(string Id, string Name, int FragmentPrice, int GMLevelRequired, int GleamLevelRequired, int PrestigePointPrice);

    /// <summary>Reads every row of an "unlock by short id" shop table (EmoteShop, IntroShop - same
    /// shape: m_RowName + four price/level-gate fields) into a list, id-and-name included. Returns
    /// an empty list if the table isn't found under <paramref name="dir"/>.</summary>
    public static List<NameCatalogItem> BuildNameCatalog(string dir, string tableName)
    {
        var result = new List<NameCatalogItem>();
        var path = FindAssetFile(dir, tableName);
        if (path == null) return result;
        var asset = Load(path);
        var table = GetTable(asset);
        if (table == null) return result;

        foreach (var key in GetRowKeys(table))
        {
            var fields = GetFields(asset, key);
            int Get(string name) => (fields.FirstOrDefault(p => p.Name.ToString() == name) as IntPropertyData)?.Value ?? 0;
            var id = (fields.FirstOrDefault(p => p.Name.ToString() == "m_RowName") as NamePropertyData)?.Value.ToString() ?? key;
            result.Add(new NameCatalogItem(id, key, Get("m_iFragmentPrice"), Get("m_iGMLevelRequired"), Get("m_iGleamLevelRequired"), Get("m_iPrestigePointPrice")));
        }
        return result;
    }

    /// <summary>Loads an item's own DataAsset (found by the short object name - the part of
    /// <paramref name="fullPath"/> after the last '.') to read its real <c>m_ItemName</c> and
    /// <c>m_EquipmentSlot</c>. Falls back to the bare short name / "" when the file isn't found
    /// under <paramref name="dir"/> or doesn't have those fields (e.g. a loot-list entry whose
    /// asset wasn't part of whatever was extracted).</summary>
    static (string DisplayName, string Slot) ResolveItemNameAndSlot(string dir, string fullPath)
    {
        var shortName = fullPath.Split('.')[^1];
        var path = FindAssetFile(dir, shortName);
        if (path == null) return (shortName, "");
        try
        {
            var asset = Load(path);
            var fields = GetFields(asset, null);
            var name = (fields.FirstOrDefault(p => p.Name.ToString() == "m_ItemName") as TextPropertyData)?.CultureInvariantString?.ToString();
            var slot = (fields.FirstOrDefault(p => p.Name.ToString() == "m_EquipmentSlot")?.ToString() ?? "").Split("::")[^1];
            // Masks are a real, distinct category in-game but their DataAsset never has an
            // m_EquipmentSlot field at all (confirmed on every mask checked, dedicated Masks/
            // folder and scattered SingleEquipment*/ ones alike) - every one does have "Mask" in
            // its own short name, so that's the only reliable signal available for them.
            if (slot.Length == 0 && shortName.Contains("Mask", StringComparison.OrdinalIgnoreCase)) slot = "Mask";
            return (string.IsNullOrWhiteSpace(name) ? shortName : name, slot);
        }
        catch
        {
            return (shortName, ""); // not worth failing the whole catalog build over one unreadable item
        }
    }

    /// <summary>Resolves and decodes the texture a <see cref="SoftObjectPropertyData"/> field
    /// points at (e.g. a move's <c>m_AttackPicto</c>, an item's <c>m_InventoryIcon</c>) - null if
    /// the field is unset, the file isn't found under <paramref name="dir"/>, or
    /// <see cref="TextureDecoder"/> can't decode it (compressed-with-out-of-line-mips, unsupported
    /// format, etc.). Shared by the GUI's live icon preview and `export-gamedata`'s icon export so
    /// both go through one path-parsing implementation.</summary>
    public static TextureDecoder.DecodedTexture? TryDecodeIcon(string dir, SoftObjectPropertyData? soft)
    {
        if (soft == null) return null;
        // This game's data stores the whole "/Game/Path/Asset.Asset" string in AssetName
        // (PackageName/SubPathString are left empty) - same fact RecipeEngine.SetValue's
        // SoftObjectPropertyData case documents. The short file name is the last path segment,
        // before its own ".Asset" repeat.
        var fullPath = soft.Value.AssetPath.AssetName.ToString();
        if (string.IsNullOrEmpty(fullPath) || fullPath == "None") return null;
        var shortName = fullPath.Split('/')[^1].Split('.')[0];
        return TryDecodeIconByAssetName(dir, shortName);
    }

    /// <summary>Same decode as <see cref="TryDecodeIcon"/> but given the texture's own short asset
    /// name directly, for the case where nothing on the item's own DataAsset references it - e.g.
    /// a power's <c>UsableItemData</c> has no <c>m_InventoryIcon</c>-shaped field at all (confirmed
    /// 2026-09-13 - `Shield`'s own asset carries only `playFeedBack`/`m_AbsorbPropertyDB`), but its
    /// real shop/inventory picto still exists as its own standalone `Texture2D` asset, found by
    /// naming convention (`PictoItem_&lt;PowerName&gt;_SansFond`) rather than a property reference.</summary>
    public static TextureDecoder.DecodedTexture? TryDecodeIconByAssetName(string dir, string shortName)
    {
        var texPath = FindAssetFile(dir, shortName);
        if (texPath == null) return null;
        var texAsset = Load(texPath);
        var texExport = texAsset.Exports.OfType<NormalExport>().FirstOrDefault();
        return texExport == null ? null : TextureDecoder.TryDecode(texExport);
    }
}
