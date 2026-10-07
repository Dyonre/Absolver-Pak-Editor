using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace AbsolverModTool.Core;

/// <summary>
/// Keeps an attack animation's baked notifies in step with its <c>attacks</c> row. An attack's timing is stored twice - the row
/// (<c>m_iFrame*</c>) and the animation's <c>Phase : BuildUp/Strike/Release</c> + <c>A.L :</c> notifies (docs/attack-row-and-notify-map.md) -
/// and the notifies are exactly a function of the row (checked on all 247 vanilla moves: strike starts 247/247, release start 247/247,
/// interruptible start 245/245 present, perfect-release start 234/234 present). So when a row's timeline is edited, the animation's
/// notifies are moved onto the new boundaries with <see cref="AnimGraft.Retime"/>; every other notify (sounds, FX, tracking, cancel)
/// is carried along proportionally inside its phase. Only notify times change; the motion data is untouched.
/// </summary>
public static class AnimRetime
{
    /// <summary>The row fields that drive the notify timeline. Editing any of these (on a row with a retime-anim edit) re-applies the retime.</summary>
    public static readonly string[] TimelineFields =
    {
        "m_iFrameBuildUp", "m_iFrameStrike1", "m_iFrameStrike2", "m_iFrameStrike3", "m_iFrameStrike4",
        "m_iFrameInterStrike1", "m_iFrameInterStrike2", "m_iFrameInterStrike3",
        "m_iFrameRelease", "m_iFrameInterruptibleRelease", "m_iFramePerfectRelease", "m_iNbFramesPerfectRelease",
    };

    public static bool IsTimelineField(string? name) => name != null && TimelineFields.Contains(name);

    /// <summary>Timeline of a row in frames at 30 fps.</summary>
    public record Timing(int Build, int[] Strikes, int[] Inter, int Release, int Interruptible, int Perfect, int PerfectWindow);

    public static Timing ReadTiming(IEnumerable<PropertyData> rowFields)
    {
        var f = rowFields.ToList();
        int I(string n) => f.OfType<IntPropertyData>().FirstOrDefault(p => p.Name.ToString() == n)?.Value ?? 0;
        var strikes = Enumerable.Range(1, 4).Select(i => I("m_iFrameStrike" + i)).Where(v => v > 0).ToArray();
        var inter = Enumerable.Range(1, 3).Select(i => I("m_iFrameInterStrike" + i)).ToArray();
        return new Timing(I("m_iFrameBuildUp"), strikes, inter, I("m_iFrameRelease"), I("m_iFrameInterruptibleRelease"), I("m_iFramePerfectRelease"), I("m_iNbFramesPerfectRelease"));
    }

    public record Notify(string Name, double Start, double Duration);

    public static IEnumerable<Notify> ReadNotifies(NormalExport anim)
    {
        var arr = anim.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Notifies");
        if (arr == null) yield break;
        foreach (var n in arr.Value.OfType<StructPropertyData>())
        {
            var link = n.Value.OfType<FloatPropertyData>().FirstOrDefault(p => p.Name.ToString() == "LinkValue");
            var dur = n.Value.OfType<FloatPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Duration");
            var name = n.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.ToString() == "NotifyName")?.Value?.ToString() ?? "";
            if (link != null && dur != null) yield return new Notify(name, link.Value, dur.Value);
        }
    }

    /// <summary>Phase boundaries implied by a row, in frames at 30 fps, keyed by what they bound.</summary>
    static Dictionary<string, double> Boundaries(Timing t, int strikeCount)
    {
        var b = new Dictionary<string, double> { ["buildEnd"] = t.Build };
        double cur = t.Build;
        for (int i = 0; i < strikeCount; i++)
        {
            b[$"s{i}a"] = cur; cur += t.Strikes[i]; b[$"s{i}b"] = cur;
            if (i < strikeCount - 1) cur += t.Inter[i];
        }
        b["relA"] = cur; b["relB"] = cur + t.Release + t.Interruptible;
        b["perfA"] = cur + t.Perfect; b["perfB"] = cur + t.Perfect + t.PerfectWindow;
        b["intrA"] = cur + t.Release; b["intrB"] = cur + t.Release + t.Interruptible;
        return b;
    }

    /// <summary>Piecewise-linear map that moves the animation's phase notifies by however much the row's boundaries moved between
    /// <paramref name="original"/> (the row this animation was authored for) and <paramref name="edited"/>. Using the *difference* keeps every
    /// quirk of the shipped animations (13 vanilla moves put the perfect-release window somewhere other than the row says) and makes an
    /// unedited row an exact identity. Null (with the reason) if they can't be paired, e.g. a different number of strikes than the animation.</summary>
    public static AnimGraft.Retime? BuildRetime(NormalExport anim, Timing edited, Timing original, double sequenceLength, out string? problem)
    {
        problem = null;
        var notes = ReadNotifies(anim).ToList();
        var build = notes.Where(n => n.Name == "Phase : BuildUp").OrderBy(n => n.Start).FirstOrDefault();
        var strikes = notes.Where(n => n.Name == "Phase : Strike").OrderBy(n => n.Start).ToList();
        var release = notes.Where(n => n.Name == "Phase : Release").OrderBy(n => n.Start).FirstOrDefault();
        var perfect = notes.FirstOrDefault(n => n.Name == "A.L : Perfect release");
        var intr = notes.FirstOrDefault(n => n.Name == "A.L : interruptible release");
        if (build == null || release == null || strikes.Count == 0) { problem = "animation has no Phase : BuildUp/Strike/Release notifies (not an attack animation?)"; return null; }
        if (strikes.Count != edited.Strikes.Length) { problem = $"row has {edited.Strikes.Length} strike(s) but the animation has {strikes.Count}; use an animation with {edited.Strikes.Length} strike notifies"; return null; }
        if (original.Strikes.Length != edited.Strikes.Length) { problem = "the animation was authored for a row with a different strike count"; return null; }

        const double fps = 30.0;
        var e = Boundaries(edited, strikes.Count); var o = Boundaries(original, strikes.Count);
        double D(string k) => (e[k] - o[k]) / fps;     // how far this boundary moved
        var pairs = new List<(double d, double t)> { (0, 0) };
        void P(double donor, string key) => pairs.Add((donor, donor + D(key)));
        P(build.Start + build.Duration, "buildEnd");
        for (int i = 0; i < strikes.Count; i++) { P(strikes[i].Start, $"s{i}a"); P(strikes[i].Start + strikes[i].Duration, $"s{i}b"); }
        P(release.Start, "relA"); P(release.Start + release.Duration, "relB");
        if (perfect != null) { P(perfect.Start, "perfA"); P(perfect.Start + perfect.Duration, "perfB"); }
        if (intr != null) { P(intr.Start, "intrA"); P(intr.Start + intr.Duration, "intrB"); }
        // Everything after the last phase boundary (and the clip length itself) is carried along unchanged.
        double lastDonor = pairs.Max(p => p.d), lastTarget = pairs.Max(p => p.t);
        pairs.Add((Math.Max(sequenceLength, lastDonor), Math.Max(sequenceLength, lastTarget)));

        pairs = pairs.OrderBy(p => p.d).ThenBy(p => p.t).ToList();
        var d = new double[pairs.Count]; var tt = new double[pairs.Count];
        for (int i = 0; i < pairs.Count; i++) { d[i] = pairs[i].d; tt[i] = i == 0 ? Math.Max(0, pairs[i].t) : Math.Max(pairs[i].t, tt[i - 1]); }   // targets never go backwards
        return new AnimGraft.Retime(d, tt);
    }

    public record Result(string OutPath, bool Changed, string? Problem, double RowEndSeconds, double ClipSeconds);

    /// <summary>Loads <paramref name="animUasset"/>, moves its notifies by the difference between <paramref name="original"/> and <paramref name="timing"/>, writes <paramref name="outPath"/>.</summary>
    public static Result RetimeFile(string animUasset, Timing timing, Timing original, string outPath, Action<string>? log = null)
    {
        log ??= _ => { };
        var asset = AssetAccess.Load(animUasset);
        var anim = AnimGraft.FindAnimSequence(asset, animUasset);
        var lenProp = anim.Data.OfType<FloatPropertyData>().FirstOrDefault(p => p.Name.ToString() == "SequenceLength");
        double len = lenProp?.Value ?? 0;
        var retime = BuildRetime(anim, timing, original, len, out var problem);
        double rowEnd = (timing.Build + timing.Strikes.Sum() + timing.Inter.Take(Math.Max(0, timing.Strikes.Length - 1)).Sum() + timing.Release + timing.Interruptible) / 30.0;
        if (retime == null) return new Result(outPath, false, problem, rowEnd, len);
        AnimGraft.RetimeNotifies(anim, retime, (float)len, log);
        if (rowEnd > len + 0.02) log($"WARNING row timeline ends at {rowEnd:0.000}s but the animation is {len:0.000}s long; late notifies fall past the end of the clip");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        asset.Write(outPath);
        return new Result(outPath, true, null, rowEnd, len);
    }

    /// <summary>Finds the .uasset for a game path like /Game/Animations/x/Y.Y under a source folder that may be rooted at the extraction,
    /// Absolver/Content or Content.</summary>
    public static string? FindByGamePath(string srcDir, string gamePath)
    {
        var rel = gamePath.Trim();
        if (rel.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) rel = rel["/Game/".Length..];
        var dot = rel.LastIndexOf('.'); if (dot > rel.LastIndexOf('/')) rel = rel[..dot];
        rel = rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset";
        foreach (var root in new[] { srcDir, Path.Combine(srcDir, "Content"), Path.Combine(srcDir, "Absolver", "Content") })
        {
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
