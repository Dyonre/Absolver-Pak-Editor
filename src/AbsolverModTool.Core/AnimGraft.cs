using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace AbsolverModTool.Core;

/// <summary>Builds a working Absolver attack animation out of two cooked AnimSequence assets: a
/// <b>donor</b> (a real attack shipped with the game, which carries the game-specific notifies - hit
/// windows, tracking, availability layers, stamina cost, footstep/sound events - plus its skeleton
/// reference and root-motion settings) and a <b>source</b> (an animation cooked in our own UE 4.18.3
/// project, which has the motion we want but none of those classes, since they only exist in the
/// game's C++ module). Rather than re-creating the notify objects, this keeps the donor asset whole
/// and swaps in the source's motion: the compressed track blob UAssetAPI exposes as the AnimSequence
/// export's <c>Extras</c>, plus the handful of tagged properties that describe that blob.</summary>
public static class AnimGraft
{
    /// <summary>Tagged properties on the AnimSequence that describe the motion data in Extras and so
    /// must travel with it. Everything else (Notifies, Skeleton, root-motion flags, curves) stays the
    /// donor's.</summary>
    public static readonly string[] MotionProperties =
    {
        "NumFrames", "SequenceLength", "RateScale",
        "TrackToSkeletonMapTable", "CompressionScheme",
        "TranslationCompressionFormat", "RotationCompressionFormat", "ScaleCompressionFormat",
        "KeyEncodingFormat", "CompressedTrackOffsets", "CompressedScaleOffsets",
    };

    public static NormalExport FindAnimSequence(UAsset asset, string label) =>
        asset.Exports.OfType<NormalExport>().FirstOrDefault(e => e.GetExportClassType().ToString() == "AnimSequence")
        ?? throw new InvalidOperationException($"{label} has no AnimSequence export.");

    /// <summary>One line per export, then every tagged property of the AnimSequence export and the
    /// size of its trailing binary blob - what <c>anim-info</c> prints.</summary>
    public static List<string> Describe(string path)
    {
        var asset = AssetAccess.Load(path);
        var lines = new List<string> { $"{Path.GetFileName(path)}: {asset.Exports.Count} export(s), {asset.Imports.Count} import(s), {asset.GetNameMapIndexList().Count} name(s)" };
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var e = asset.Exports[i];
            var extras = e is NormalExport n ? n.Extras?.Length ?? 0 : 0;
            lines.Add($"  [{i + 1}] {e.GetExportClassType(),-36} {e.ObjectName,-40} extras={extras}");
        }
        var anim = FindAnimSequence(asset, path);
        lines.Add($"AnimSequence '{anim.ObjectName}' properties:");
        foreach (var p in anim.Data) lines.Add("  " + DescribeProperty(asset, p));
        if (anim.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Notifies") is { } notifies)
        {
            if (notifies.Value.FirstOrDefault() is UAssetAPI.PropertyTypes.Structs.StructPropertyData first)
            {
                lines.Add("Notify struct fields (first notify):");
                foreach (var f in first.Value)
                    lines.Add("    " + DescribeProperty(asset, f) + (f is UAssetAPI.PropertyTypes.Structs.StructPropertyData s
                        ? "  { " + string.Join(", ", s.Value.Select(x => $"{x.Name}={x.RawValue}")) + " }" : ""));
            }
            lines.Add("Notifies:");
            foreach (var n in notifies.Value.Cast<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
                lines.Add("  " + string.Join("  ", n.Value
                    .Where(f => f is FloatPropertyData or ObjectPropertyData or NamePropertyData)
                    .Where(f => f is not ObjectPropertyData o || o.Value.Index != 0)
                    .Select(f => $"{f.Name}={(f is ObjectPropertyData o ? (o.Value.IsExport() ? o.Value.ToExport(asset).ObjectName.ToString() : o.Value.ToImport(asset).ObjectName.ToString()) : f.RawValue)}")));
        }
        lines.Add($"  (binary blob after properties: {anim.Extras?.Length ?? 0} bytes)");
        if (anim.Extras is { Length: > 0 } blob)
        {
            lines.Add("  blob head: " + Convert.ToHexString(blob, 0, Math.Min(64, blob.Length)));
            lines.Add("  blob tail: " + Convert.ToHexString(blob, Math.Max(0, blob.Length - 32), Math.Min(32, blob.Length)));
        }
        lines.Add("  custom versions: " + string.Join(", ", asset.CustomVersionContainer.Select(v => $"{v.FriendlyName ?? v.Key.ToString()}={v.Version}")));
        return lines;
    }

    public record GraftResult(string OutPath, int Frames, float Length, int BlobBytes, int NotifyCount, string? NewPackagePath);

    /// <summary>Writes a copy of <paramref name="donorPath"/> whose motion is
    /// <paramref name="sourcePath"/>'s. With <paramref name="newPackagePath"/> (e.g.
    /// <c>/Game/Animations/Attacks/Custom/MyMove</c>) the copy is also renamed so it can ship as a new
    /// asset next to the vanilla one; without it the output still claims the donor's own path, i.e. it
    /// overrides the donor in-game.
    /// <para>Notify times are the donor's and are in seconds, so a source much shorter than the donor
    /// leaves notifies past the end - pick a donor with similar timing, or retime afterwards.</para></summary>
    /// <summary>Piecewise-linear time map from the donor's phase boundaries to the new move's, both in
    /// seconds and both starting at 0 and ending at the sequence length. Absolver's attack notifies are
    /// laid out straight from the row's frame data (BuildUp 0..B, Strike, InterStrike, Release, the two
    /// availability windows), so mapping boundary to boundary moves every phase to the new timing and
    /// carries the sound/footstep notifies along proportionally inside their phase.</summary>
    public record Retime(double[] DonorKnots, double[] TargetKnots)
    {
        public double Map(double t)
        {
            if (t <= DonorKnots[0]) return TargetKnots[0];
            for (int i = 1; i < DonorKnots.Length; i++)
            {
                if (t > DonorKnots[i]) continue;
                double span = DonorKnots[i] - DonorKnots[i - 1];
                double f = span <= 0 ? 1 : (t - DonorKnots[i - 1]) / span;
                return TargetKnots[i - 1] + f * (TargetKnots[i] - TargetKnots[i - 1]);
            }
            return TargetKnots[^1];
        }

        /// <summary>Knots given as cumulative frame counts at 30 fps, e.g. "0,13,15,24,43".</summary>
        public static Retime FromFrames(string donorFrames, string targetFrames)
        {
            static double[] Parse(string s) => s.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) / 30.0).ToArray();
            var d = Parse(donorFrames);
            var t = Parse(targetFrames);
            if (d.Length != t.Length || d.Length < 2) throw new ArgumentException($"Retime needs the same number of donor and target boundaries (got {d.Length} and {t.Length}).");
            if (d[0] != 0 || t[0] != 0) throw new ArgumentException("Retime boundaries must start at frame 0.");
            for (int i = 1; i < d.Length; i++)
                if (d[i] < d[i - 1] || t[i] < t[i - 1]) throw new ArgumentException("Retime boundaries must not decrease.");
            return new Retime(d, t);
        }
    }

    public static void RetimeNotifies(NormalExport anim, Retime retime, float newLength, Action<string> log)
    {
        var notifies = anim.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Notifies");
        if (notifies == null) { log("no Notifies array to retime"); return; }
        static FloatPropertyData? F(IEnumerable<PropertyData> fields, string name) => fields.OfType<FloatPropertyData>().FirstOrDefault(p => p.Name.ToString() == name);
        foreach (var n in notifies.Value.Cast<UAssetAPI.PropertyTypes.Structs.StructPropertyData>())
        {
            var link = F(n.Value, "LinkValue");
            var duration = F(n.Value, "Duration");
            if (link == null || duration == null) throw new InvalidDataException("Notify without LinkValue/Duration - not the layout this was written against.");
            double start = link.Value, end = start + duration.Value;
            double newStart = retime.Map(start), newEnd = retime.Map(end);
            var name = n.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.ToString() == "NotifyName")?.Value?.ToString() ?? "?";
            log($"  notify {name,-30} {start,6:0.000}+{duration.Value,5:0.000} -> {newStart,6:0.000}+{newEnd - newStart,5:0.000}");
            link.Value = (float)newStart;
            duration.Value = duration.Value > 0 ? (float)(newEnd - newStart) : 0f;
            if (F(n.Value, "SegmentLength") is { } seg) seg.Value = newLength;
            if (n.Value.OfType<UAssetAPI.PropertyTypes.Structs.StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "EndLink") is { } endLink)
            {
                if (F(endLink.Value, "LinkValue") is { } endValue) endValue.Value = (float)newEnd;
                if (F(endLink.Value, "SegmentLength") is { } endSeg) endSeg.Value = newLength;
            }
        }
    }

    /// <summary>Drops notify events whose NotifyName starts with one of <paramref name="prefixes"/> (e.g.
    /// "Avoid Window", the invulnerability window some boss moves carry). The notify objects stay in the
    /// package, unreferenced.</summary>
    static void StripNotifies(NormalExport anim, string[] prefixes, Action<string> log)
    {
        var notifies = anim.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Notifies");
        if (notifies == null) return;
        var keep = new List<PropertyData>();
        foreach (var n in notifies.Value)
        {
            var name = (n as UAssetAPI.PropertyTypes.Structs.StructPropertyData)?.Value.OfType<NamePropertyData>()
                .FirstOrDefault(p => p.Name.ToString() == "NotifyName")?.Value?.ToString() ?? "";
            if (prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { log($"  stripped notify '{name}'"); continue; }
            keep.Add(n);
        }
        notifies.Value = keep.ToArray();
    }

    public static GraftResult Graft(string donorPath, string sourcePath, string outPath, string? newPackagePath = null, Action<string>? log = null, Retime? retime = null, string[]? strip = null, int? numFramesOverride = null)
    {
        log ??= _ => { };
        var donor = AssetAccess.Load(donorPath);
        var source = AssetAccess.Load(sourcePath);
        var dAnim = FindAnimSequence(donor, "Donor");
        var sAnim = FindAnimSequence(source, "Source");
        if (sAnim.Extras == null || sAnim.Extras.Length == 0)
            throw new InvalidOperationException("Source AnimSequence has no compressed track data after its properties - was it cooked?");

        // Both sides must drive the same bones in the same order, or the donor's bone/track tables
        // would point the new tracks at the wrong bones.
        var dMap = TrackMap(dAnim);
        var sMap = TrackMap(sAnim);
        if (!dMap.SequenceEqual(sMap))
            throw new InvalidOperationException(
                $"Track-to-bone tables differ (donor {dMap.Count} tracks, source {sMap.Count}) - the source must animate the same skeleton bones in the same order as the donor.");
        log($"track tables match ({dMap.Count} tracks)");

        var sFrames = sAnim.Data.OfType<IntPropertyData>().First(p => p.Name.ToString() == "NumFrames").Value;
        var sLength = sAnim.Data.OfType<FloatPropertyData>().First(p => p.Name.ToString() == "SequenceLength").Value;
        var dFrames = dAnim.Data.OfType<IntPropertyData>().First(p => p.Name.ToString() == "NumFrames");
        var dLength = dAnim.Data.OfType<FloatPropertyData>().First(p => p.Name.ToString() == "SequenceLength");
        log($"NumFrames {dFrames.Value} -> {sFrames}, SequenceLength {dLength.Value} -> {sLength}");
        // --num-frames: the engine plays the compressed tracks by their own per-track key count and the
        // SequenceLength (AnimEncoding.h TimeToIndex, ConstantKeyLerp), so the NumFrames property can differ from
        // the key count. Absolver reads it only as the denominator of the phase ratios (OrderAttack::
        // ComputePhaseInfo: row frames at 30 fps / NumFrames), so a 60 Hz clip can report its 30 fps frame count.
        var effectiveFrames = numFramesOverride ?? sFrames;
        if (numFramesOverride != null)
            log($"NumFrames property overridden to {effectiveFrames}; the clip keeps its {sFrames} keys over {sLength} s ({(sFrames - 1) / sLength:0.#} keys/s)");
        dFrames.Value = effectiveFrames;
        dLength.Value = sLength;

        // Anything else on the source that describes its motion but that the donor doesn't carry would
        // be silently lost - surface it instead of guessing.
        foreach (var p in sAnim.Data)
        {
            var name = p.Name.ToString();
            if (name is "NumFrames" or "SequenceLength" or "TrackToSkeletonMapTable" or "Skeleton") continue;
            log($"WARNING source property '{name}' ({p.PropertyType}) was not carried over");
        }

        if (dAnim.Extras == null) throw new InvalidOperationException("Donor AnimSequence has no data after its properties.");
        var grafted = SpliceBlob(dAnim.Extras, sAnim.Extras, log);
        log($"motion blob {dAnim.Extras.Length} -> {grafted.Length} bytes");
        dAnim.Extras = grafted;

        if (strip is { Length: > 0 }) StripNotifies(dAnim, strip, log);

        if (retime != null)
        {
            if (Math.Abs(retime.TargetKnots[^1] - sLength) > 0.02)
                log($"WARNING retime ends at {retime.TargetKnots[^1]:0.000} s but the new sequence is {sLength:0.000} s long");
            log("retiming notifies to the new phase boundaries:");
            RetimeNotifies(dAnim, retime, sLength, log);
        }

        var notifies = dAnim.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Notifies");
        log($"kept donor's {notifies?.Value.Length ?? 0} notify event(s), {donor.Exports.Count - 1} notify object(s), root motion and skeleton reference");

        if (newPackagePath != null) Rename(donor, dAnim, newPackagePath, log);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        donor.Write(outPath);

        // Reload what was just written: a graft that UAssetAPI itself cannot read back is not worth packing.
        var check = FindAnimSequence(AssetAccess.Load(outPath), "Grafted output");
        if (check.Extras == null || !check.Extras.SequenceEqual(grafted))
            throw new InvalidOperationException("Grafted output did not round-trip: motion blob differs after reload.");
        log($"wrote {outPath} and verified it reloads with the new motion");
        return new GraftResult(outPath, effectiveFrames, sLength, sAnim.Extras.Length, notifies?.Value.Length ?? 0, newPackagePath);
    }

    /// <summary>Where the pieces of a cooked UE 4.18 AnimSequence's post-property data sit. Layout
    /// (UAnimationAsset/UAnimSequence::Serialize), confirmed against both the game's and our own cooked
    /// assets: SkeletonGuid (16) | strip flags (2) | bSerializeCompressedData (4) | key/translation/
    /// rotation/scale formats (4 x 1) | CompressedTrackOffsets (count + int32s) | CompressedScaleOffsets
    /// (count + int32s + strip size) | track-to-skeleton table (count + int32s) | CompressedCurveData
    /// (tagged properties) | byte stream (count + bytes) | bUseRawDataOnly (4).</summary>
    readonly record struct BlobLayout(int CurveStart, int CurveEnd);

    const int SkeletonGuidSize = 16;
    const int EmptyCurveBlockSize = 98;

    static BlobLayout ParseBlob(byte[] blob, string label)
    {
        int pos = SkeletonGuidSize + 2;
        int ReadInt() { var v = BitConverter.ToInt32(blob, pos); pos += 4; return v; }
        void SkipIntArray(string what)
        {
            int count = ReadInt();
            if (count < 0 || pos + (long)count * 4 > blob.Length)
                throw new InvalidDataException($"{label}: implausible {what} count {count} at offset {pos - 4} - not the UE 4.18 cooked AnimSequence layout.");
            pos += count * 4;
        }
        if (ReadInt() != 1) throw new InvalidDataException($"{label}: bSerializeCompressedData is not 1 - asset has no cooked compressed tracks.");
        pos += 4;                                   // four one-byte format enums
        SkipIntArray("CompressedTrackOffsets");
        SkipIntArray("CompressedScaleOffsets");
        pos += 4;                                   // CompressedScaleOffsets.StripSize
        SkipIntArray("track-to-skeleton table");
        int curveStart = pos;

        // The curve block is tagged properties whose length isn't stored anywhere, so find its end from
        // the back instead: the byte stream that follows is [count][count bytes] and then exactly one
        // int32 (bUseRawDataOnly) closes the blob. The curve block is at least one 8-byte "None" name.
        for (int p = curveStart + 8; p + 8 <= blob.Length; p++)
        {
            int numBytes = BitConverter.ToInt32(blob, p);
            if (numBytes > 0 && p + 4 + (long)numBytes + 4 == blob.Length) return new BlobLayout(curveStart, p);
        }
        throw new InvalidDataException($"{label}: could not locate the compressed byte stream after the curve block (curve block starts at {curveStart}).");
    }

    /// <summary>Donor's SkeletonGuid and curve block, source's everything else. The SkeletonGuid must
    /// stay the game skeleton's, not our project's. The curve block must stay the donor's because it is
    /// tagged-property data full of name-map indices: the source's indices point into the source
    /// asset's name map and mean something else entirely inside the donor (the first graft attempt
    /// copied the whole blob and came out undecodable for exactly this reason).</summary>
    public static byte[] SpliceBlob(byte[] donor, byte[] source, Action<string> log)
    {
        var d = ParseBlob(donor, "Donor");
        var s = ParseBlob(source, "Source");
        int sCurveLen = s.CurveEnd - s.CurveStart;
        log($"blob layout: donor curve block {d.CurveEnd - d.CurveStart} bytes at {d.CurveStart}, source curve block {sCurveLen} bytes at {s.CurveStart}");
        // A stock-cooked sequence with no curves still writes a 98-byte block (two empty curve arrays).
        // Anything bigger means the source really has curves, and those are dropped here.
        if (sCurveLen > EmptyCurveBlockSize)
            log($"WARNING source has real curve data ({sCurveLen} bytes) - it is NOT carried over; the donor's curve block is used instead");

        using var ms = new MemoryStream();
        ms.Write(donor, 0, SkeletonGuidSize);
        ms.Write(source, SkeletonGuidSize, s.CurveStart - SkeletonGuidSize);
        ms.Write(donor, d.CurveStart, d.CurveEnd - d.CurveStart);
        ms.Write(source, s.CurveEnd, source.Length - s.CurveEnd);
        return ms.ToArray();
    }

    static List<int> TrackMap(NormalExport anim)
    {
        var table = anim.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "TrackToSkeletonMapTable")
            ?? throw new InvalidOperationException($"AnimSequence '{anim.ObjectName}' has no TrackToSkeletonMapTable.");
        return table.Value.Cast<UAssetAPI.PropertyTypes.Structs.StructPropertyData>()
            .Select(s => s.Value.OfType<IntPropertyData>().First().Value).ToList();
    }

    /// <summary>Points the asset at a new package path. In a cooked 4.18 asset the package path and
    /// the object name are plain name-map entries, so rewriting those two strings renames it; the
    /// notify exports reference the AnimSequence by index and follow along.</summary>
    public static void Rename(UAsset asset, NormalExport anim, string newPackagePath, Action<string> log)
    {
        if (!newPackagePath.StartsWith("/Game/", StringComparison.Ordinal) || newPackagePath.EndsWith('/'))
            throw new ArgumentException($"New package path must look like /Game/Folder/AssetName, got '{newPackagePath}'.");
        var newName = newPackagePath[(newPackagePath.LastIndexOf('/') + 1)..];
        var oldName = anim.ObjectName.ToString();
        var names = asset.GetNameMapIndexList();
        int renamedPaths = 0, renamedNames = 0;
        for (int i = 0; i < names.Count; i++)
        {
            var value = names[i].Value;
            if (value.StartsWith("/Game/", StringComparison.Ordinal) && value.EndsWith("/" + oldName, StringComparison.Ordinal))
            {
                asset.SetNameReference(i, new UAssetAPI.UnrealTypes.FString(newPackagePath));
                log($"package path '{value}' -> '{newPackagePath}'");
                renamedPaths++;
            }
            else if (value == oldName)
            {
                asset.SetNameReference(i, new UAssetAPI.UnrealTypes.FString(newName));
                log($"object name '{value}' -> '{newName}'");
                renamedNames++;
            }
        }
        if (renamedNames != 1)
            throw new InvalidOperationException($"Expected exactly one name-map entry '{oldName}' to rename, found {renamedNames}.");
        if (renamedPaths == 0)
            log("note: no package-path entry in the name map (the path is implied by where the file sits in the pak)");
    }

    static string DescribeProperty(UAsset asset, PropertyData p)
    {
        string value = p switch
        {
            ArrayPropertyData a => $"[{a.Value.Length} x {a.ArrayType}]",
            ObjectPropertyData o => o.Value.Index == 0 ? "null" : o.Value.IsImport() ? $"import:{o.Value.ToImport(asset).ObjectName}" : $"export:{o.Value.ToExport(asset).ObjectName}",
            _ => p.RawValue?.ToString() ?? "null",
        };
        return $"{p.Name,-34} {p.PropertyType,-18} {value}";
    }
}
