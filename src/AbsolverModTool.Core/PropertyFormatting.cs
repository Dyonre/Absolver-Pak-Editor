using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace AbsolverModTool.Core;

public static class PropertyFormatting
{
    public static string Label(PropertyData p) =>
        p.ArrayIndex == 0 ? p.Name.ToString() : $"{p.Name}[{p.ArrayIndex}]";

    // Recurses into StructProperty/ArrayProperty so nested fields (e.g. a combat deck's
    // quadrant arrays) print as a compact inline value instead of a bare .NET type name.
    public static string Format(PropertyData p, UAsset asset)
    {
        if (p is ObjectPropertyData op)
        {
            if (op.IsNull()) return "None";
            if (op.IsImport()) return "import:" + op.ToImport(asset).ObjectName;
            if (op.IsExport()) return "export:" + op.ToExport(asset).ObjectName;
        }
        if (p is StructPropertyData sp)
        {
            var parts = sp.Value.Select(inner => $"{inner.Name}={Format(inner, asset)}");
            return "{" + string.Join(", ", parts) + "}";
        }
        if (p is ArrayPropertyData ap)
        {
            var parts = ap.Value.Select(inner => Format(inner, asset));
            return "[" + string.Join(", ", parts) + "]";
        }
        return p.ToString() ?? "None";
    }
}
