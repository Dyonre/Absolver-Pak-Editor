using UAssetAPI;

namespace AbsolverModTool.Core;

public record RoundTripResult(string RelativePath, bool Ok, bool Threw, string? Error);

public static class RoundTripEngine
{
    public static List<RoundTripResult> Run(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Directory not found: {dir}");

        var uassetFiles = Directory.GetFiles(dir, "*.uasset", SearchOption.AllDirectories);
        var results = new List<RoundTripResult>();

        foreach (var path in uassetFiles.OrderBy(p => p))
        {
            var rel = Path.GetRelativePath(dir, path);
            try
            {
                var asset = AssetAccess.Load(path);
                bool ok = asset.VerifyBinaryEquality();
                results.Add(new RoundTripResult(rel, ok, false, ok ? null : "binary mismatch after round-trip"));
            }
            catch (Exception ex)
            {
                results.Add(new RoundTripResult(rel, false, true, ex.Message));
            }
        }

        return results;
    }
}
