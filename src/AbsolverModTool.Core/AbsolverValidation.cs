using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace AbsolverModTool.Core;

// Absolver-specific sanity checks - the domain knowledge that makes this tool better than a
// generic asset editor. Kept in Core so both the CLI and GUI see the same warnings.
public static class AbsolverValidation
{
    static readonly string[] GearTableNameHints = { "Equipment", "Gear", "Weapon" };

    /// <summary>
    /// Checks a freshly-cloned row for the mistakes the roadmap calls out explicitly:
    /// an accidental m_bIsDrunkenAttack, a stat scale left at None, or a new row in a
    /// gear/equipment-shaped table (whose new ID gets baked into player save files - removing
    /// it later, e.g. by reverting to vanilla, can crash a save that equipped it).
    /// </summary>
    public static List<string> CheckNewRow(string assetName, StructPropertyData row)
    {
        var warnings = new List<string>();

        if (row.Value.FirstOrDefault(p => p.Name.ToString() == "m_bIsDrunkenAttack") is BoolPropertyData { Value: true })
            warnings.Add("m_bIsDrunkenAttack is true - usually unintentional on a cloned row.");

        foreach (var scaleField in new[] { "m_eStrengthScale", "m_eAgilityScale", "m_eWeightRatioScale" })
        {
            if (row.Value.FirstOrDefault(f => f.Name.ToString() == scaleField) is EnumPropertyData ep
                && ep.Value.ToString() == "None")
                warnings.Add($"{scaleField} is None - this stat scale usually needs a real value.");
        }

        if (GearTableNameHints.Any(h => assetName.Contains(h, StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"'{assetName}' looks like a gear/equipment table - new IDs from here get saved into " +
                "player save files. Removing this row later (e.g. reverting to vanilla) can crash a save that equipped it.");

        return warnings;
    }
}
