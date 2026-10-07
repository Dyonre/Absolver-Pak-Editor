namespace AbsolverModTool.Core;

/// <summary>
/// Which properties count as "easily understood" for Simplified Mode, per asset. Everything not
/// listed here still exists and is still editable in Advanced mode - this is a display filter,
/// never a data restriction. First-pass judgment call, meant to be adjusted once someone who
/// isn't reading the code tries it.
/// </summary>
public static class SimpleModeProperties
{
    static readonly Dictionary<string, HashSet<string>> Whitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["attacks"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // m_fDamage (no suffix) is always 0 in every row checked - looks like a runtime-computed
            // field, not authored data. m_fDamageMax1 is the real first-hit damage value.
            "m_RealAttackName", "m_fDamageMax1", "m_fGuardDamage",
            "m_fKnockBackDistance", "m_fGuardKnockBackDistance",
            "m_eAttackPower", "m_iXPRequiredToUnlock",
        },
        ["EquipmentShop"] = new(StringComparer.OrdinalIgnoreCase) { "m_iFragmentPrice", "m_iGMLevelRequired", "m_iGleamLevelRequired" },
        ["WeaponShop"] = new(StringComparer.OrdinalIgnoreCase) { "m_iFragmentPrice", "m_iGMLevelRequired", "m_iGleamLevelRequired" },
        ["PowersShop"] = new(StringComparer.OrdinalIgnoreCase) { "m_iFragmentPrice", "m_iGleamLevelRequired" },
        ["EmoteShop"] = new(StringComparer.OrdinalIgnoreCase) { "m_iPrestigePointPrice" },
        ["IntroShop"] = new(StringComparer.OrdinalIgnoreCase) { "m_iPrestigePointPrice" },
    };

    public static bool HasWhitelist(string? assetName) => assetName != null && Whitelist.ContainsKey(assetName);

    public static bool IsSimple(string? assetName, string propertyPath)
    {
        if (assetName == null || !Whitelist.TryGetValue(assetName, out var set)) return true; // no opinion -> show it
        var baseName = propertyPath.Contains('[') ? propertyPath[..propertyPath.IndexOf('[')] : propertyPath;
        return set.Contains(baseName);
    }
}
