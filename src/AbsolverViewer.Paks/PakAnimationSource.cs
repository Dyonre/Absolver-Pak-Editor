using System.Text;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using Newtonsoft.Json.Linq;

namespace AbsolverViewer.Paks;

/// <summary>
/// Mounts a game Paks folder (base paks + any mod paks in it) and serves viewer JSON on demand. Everything is
/// cached per instance; Dispose and create a new one to pick up a changed pak set.
/// </summary>
public sealed partial class PakAnimationSource : IDisposable
{
    public const string DefaultAesKey = "c7B2DvPsRgH9Ve0BjGJKzBIeCIeoSTtX";   // Absolver base paks (docs/save-format-notes.md)
    const string ContentPrefix = "Absolver/Content/";
    const string AttacksPackage = "Absolver/Content/DB/Attacks/attacks";
    const string PlayerMesh = "Absolver/Content/Characters/official/M_ThePlainesChara";

    readonly DefaultFileProvider _provider;
    readonly Dictionary<string, string?> _animCache = new();
    readonly object _lock = new();
    string? _skeleton, _mesh, _moves;

    public Action<string>? Log { get; set; }
    public int FileCount => _provider.Files.Count;
    public IReadOnlyList<string> MountedPaks { get; }

    static bool _natives;
    static void InitNatives(Action<string>? log)
    {
        if (_natives) return;
        _natives = true;
        // Optional native decompressors (zlib-ng / Oodle, e.g. FModel's downloads): ABSOLVER_NATIVES_DIR, else a "natives" folder next to the tool.
        var dir = Environment.GetEnvironmentVariable("ABSOLVER_NATIVES_DIR") ?? Path.Combine(AppContext.BaseDirectory, "natives");
        var zlib = Path.Combine(dir, "zlib-ng2.dll");
        var oodle = Path.Combine(dir, "oodle-data-shared.dll");
        if (File.Exists(zlib)) ZlibHelper.Initialize(zlib);
        if (File.Exists(oodle)) OodleHelper.Initialize(oodle);
        if (!File.Exists(zlib) && !File.Exists(oodle)) log?.Invoke("pak: no native decompressors found (fine for Absolver's own paks; see ABSOLVER_NATIVES_DIR if a pak fails to read)");
    }

    public PakAnimationSource(string paksDir, string? aesKey = null, Action<string>? log = null)
    {
        Log = log;
        InitNatives(log);
        var key = aesKey ?? DefaultAesKey;
        var hex = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key : "0x" + Convert.ToHexString(Encoding.ASCII.GetBytes(key));
        _paksDir = paksDir; _keyHex = hex;
        _provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, true, new VersionContainer(EGame.GAME_UE4_18));
        _provider.Initialize();
        var n = _provider.SubmitKey(new FGuid(), new FAesKey(hex));
        MountedPaks = Directory.EnumerateFiles(paksDir, "*.pak").Select(p => Path.GetFileName(p)!).OrderBy(x => x).ToList();
        Log?.Invoke($"pak: mounted {n} archive(s) from {paksDir}, {_provider.Files.Count} files ({string.Join(", ", MountedPaks)})");
        if (_provider.Files.Count == 0) throw new InvalidOperationException("nothing mounted - wrong AES key or Paks folder");
    }

    /// <summary>/Game/Animations/x/y.y -> Absolver/Content/Animations/x/y (an already-converted path is returned unchanged).</summary>
    public static string ToPackagePath(string gamePath)
    {
        var p = gamePath.Trim();
        if (p.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) p = ContentPrefix + p["/Game/".Length..];
        var dot = p.LastIndexOf('.');
        if (dot > p.LastIndexOf('/')) p = p[..dot];
        return p;
    }

    static double R(double v) => Math.Round(v, 5);

    public string SkeletonJson()
    {
        lock (_lock)
        {
            if (_skeleton != null) return _skeleton;
            var sm = _provider.LoadPackage(PlayerMesh).GetExports().OfType<USkeletalMesh>().First();
            var skel = sm.Skeleton?.Load<USkeleton>() ?? throw new Exception("player skeleton did not load");
            var rsk = skel.ReferenceSkeleton;
            int bc = rsk.FinalRefBoneInfo.Length;
            var j = new JObject
            {
                ["bones"] = new JArray(Enumerable.Range(0, bc).Select(i => new JObject { ["name"] = rsk.FinalRefBoneInfo[i].Name.Text, ["parent"] = rsk.FinalRefBoneInfo[i].ParentIndex })),
                ["bindPose"] = new JArray(Enumerable.Range(0, bc).Select(i =>
                {
                    var p = rsk.FinalRefBonePose[i];
                    return new JObject
                    {
                        ["position"] = new JObject { ["x"] = R(p.Translation.X), ["y"] = R(p.Translation.Z), ["z"] = R(p.Translation.Y) },
                        ["rotation"] = new JObject { ["x"] = R(-p.Rotation.X), ["y"] = R(-p.Rotation.Z), ["z"] = R(-p.Rotation.Y), ["w"] = R(p.Rotation.W) },
                    };
                })),
            };
            return _skeleton = j.ToString(Newtonsoft.Json.Formatting.None);
        }
    }

    /// <summary>Animation JSON ({animation:{...}}), or null if the asset is missing / not an animation. Axis conversion
    /// UE -> three.js: pos (X,Z,Y), quat (-X,-Z,-Y,W); translation tracks that never move are omitted (they equal the bind pose).</summary>
    public string? AnimationJson(string gamePath)
    {
        var pkg = ToPackagePath(gamePath);
        lock (_lock)
        {
            if (_animCache.TryGetValue(pkg, out var hit)) return hit;
            string? json = null;
            try { json = BuildAnimation(pkg); }
            catch (Exception ex) { Log?.Invoke($"pak: animation {pkg} failed: {ex.GetType().Name}: {ex.Message}"); }
            return _animCache[pkg] = json;
        }
    }

    string? BuildAnimation(string pkg)
    {
        var anim = _provider.LoadPackage(pkg).GetExports().OfType<UAnimSequence>().FirstOrDefault();
        if (anim == null) return null;
        var skel = anim.Skeleton.Load<USkeleton>() ?? throw new Exception("skeleton did not load");
        var set = skel.ConvertAnims(anim);
        if (set.Sequences.Count == 0) throw new Exception("no sequence converted");
        var seq = set.Sequences[0];
        var rsk = skel.ReferenceSkeleton;
        int bc = rsk.FinalRefBoneInfo.Length;
        int nf = seq.NumFrames;
        float fps = seq.FramesPerSecond > 0 ? seq.FramesPerSecond : 30f;
        float dur = seq.AnimEndTime > 0 ? seq.AnimEndTime : (nf > 1 ? (nf - 1) / fps : 1f / fps);
        var tracks = new JArray();
        for (int bi = 0; bi < bc && bi < seq.Tracks.Count; bi++)
        {
            var tr = seq.Tracks[bi];
            if (tr == null || !tr.HasKeys()) continue;
            var times = new List<double>(); var ps = new List<JObject>(); var qs = new List<JObject>();
            double minx = 1e9, maxx = -1e9, miny = 1e9, maxy = -1e9, minz = 1e9, maxz = -1e9;
            for (int f = 0; f < nf; f++)
            {
                var q = new CUE4Parse.UE4.Objects.Core.Math.FQuat(0, 0, 0, 1);
                var t = new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
                var sc = new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1);
                tr.GetBoneTransform(f, nf, ref q, ref t, ref sc);
                times.Add(R((double)f / fps));
                double x = t.X, y = t.Z, z = t.Y;
                minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y); minz = Math.Min(minz, z); maxz = Math.Max(maxz, z);
                ps.Add(new JObject { ["x"] = R(x), ["y"] = R(y), ["z"] = R(z) });
                qs.Add(new JObject { ["x"] = R(-q.X), ["y"] = R(-q.Z), ["z"] = R(-q.Y), ["w"] = R(q.W) });
            }
            var track = new JObject { ["boneName"] = rsk.FinalRefBoneInfo[bi].Name.Text, ["boneIndex"] = bi, ["times"] = new JArray(times), ["rotations"] = new JArray(qs) };
            if (maxx - minx > 1e-4 || maxy - miny > 1e-4 || maxz - minz > 1e-4) track["positions"] = new JArray(ps);
            tracks.Add(track);
        }
        // Notifies (Phase : BuildUp/Strike/Release, A.L : ..., CancelNotify, FX, sounds ...): the cooked half of a move's timing.
        // t/d in seconds; kind is the notify object's class name when it has one (state class for notify states).
        var notifies = new JArray();
        foreach (var n in anim.Notifies ?? Array.Empty<FAnimNotifyEvent>())
        {
            var cls = n.NotifyStateClass?.ResolvedObject?.Class?.Name.Text ?? n.Notify?.ResolvedObject?.Class?.Name.Text
                      ?? (n.NotifyStateClass?.Name ?? n.Notify?.Name);
            notifies.Add(new JObject { ["name"] = n.NotifyName.Text, ["cls"] = cls ?? "", ["t"] = R(n.LinkValue + n.TriggerTimeOffset), ["d"] = R(n.Duration), ["state"] = n.NotifyStateClass != null && !n.NotifyStateClass.IsNull });
        }
        return new JObject { ["animation"] = new JObject { ["name"] = seq.Name, ["duration"] = dur, ["numFrames"] = nf, ["fps"] = fps, ["tracks"] = tracks, ["notifies"] = notifies } }
            .ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>Skinned player mesh in the viewer's mesh format.</summary>
    public string MeshJson()
    {
        lock (_lock)
        {
            if (_mesh != null) return _mesh;
            var sm = _provider.LoadPackage(PlayerMesh).GetExports().OfType<USkeletalMesh>().First();
            if (!sm.TryConvert(out var cm)) throw new Exception("mesh TryConvert failed");
            var lod = (CSkelMeshLod)cm.LODs[0];
            int n = lod.Verts.Length;
            var pos = new List<double>(); var nor = new List<double>(); var uv = new List<double>(); var si = new List<int>(); var sw = new List<double>();
            for (int i = 0; i < n; i++)
            {
                var v = lod.Verts[i];
                pos.AddRange(new[] { R(v.Position.X), R(v.Position.Z), R(v.Position.Y) });
                nor.AddRange(new[] { R(v.Normal.X), R(v.Normal.Z), R(v.Normal.Y) });
                uv.AddRange(new[] { R(v.UV.U), R(1 - v.UV.V) });
                float tot = 0; int inf = Math.Min(4, v.Influences.Count);
                for (int j = 0; j < inf; j++) tot += v.Influences[j].Weight;
                for (int j = 0; j < 4; j++)
                {
                    if (j < inf) { si.Add(v.Influences[j].Bone); sw.Add(R(tot > 0 ? v.Influences[j].Weight / tot : 0)); }
                    else { si.Add(0); sw.Add(0); }
                }
            }
            var idx = new List<int>();
            var ib = lod.Indices.Value;
            if (ib.Indices32.Length > 0) foreach (var ix in ib.Indices32) idx.Add((int)ix);
            else foreach (var ix in ib.Indices16) idx.Add(ix);
            var rs = cm.RefSkeleton;
            var bp = new List<double>();
            for (int i = 0; i < rs.Count; i++)
            {
                var b = rs[i];
                bp.AddRange(new[] { R(b.Position.X), R(b.Position.Z), R(b.Position.Y), R(-b.Orientation.X), R(-b.Orientation.Z), R(-b.Orientation.Y), R(b.Orientation.W) });
            }
            return _mesh = new JObject
            {
                ["positions"] = new JArray(pos), ["normals"] = new JArray(nor), ["uvs"] = new JArray(uv), ["indices"] = new JArray(idx),
                ["skinIndices"] = new JArray(si), ["skinWeights"] = new JArray(sw),
                ["boneNames"] = new JArray(Enumerable.Range(0, rs.Count).Select(i => rs[i].Name.Text)),
                ["bindPose"] = new JArray(bp), ["boneParents"] = new JArray(Enumerable.Range(0, rs.Count).Select(i => rs[i].ParentIndex)),
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }

    /// <summary>Every row of the attacks table (mod paks included): [{Id, Row:{rawProperty: value}}]. The viewer maps the raw
    /// row fields itself, the same code that handles live edits from the property grid.</summary>
    public string MovesJson()
    {
        lock (_lock)
        {
            if (_moves != null) return _moves;
            var table = _provider.LoadPackage(AttacksPackage).GetExports().OfType<UDataTable>().First();
            var arr = new JArray();
            foreach (var (name, row) in table.RowMap)
            {
                try
                {
                    var o = new JObject();
                    foreach (var p in row.Properties)
                        o[p.Name.Text] = JToken.FromObject(Simplify(p.Tag?.GenericValue));
                    arr.Add(new JObject { ["Id"] = name.Text, ["Row"] = o });
                }
                catch (Exception ex) { Log?.Invoke($"pak: attacks row {name.Text} skipped: {ex.Message}"); }
            }
            AttachSources(arr);   // adds Src / Status per row (which pak a move comes from)
            Log?.Invoke($"pak: attacks table has {arr.Count} rows ({arr.Count(r => (string?)r["Status"] == "new")} new, {arr.Count(r => (string?)r["Status"] == "changed")} changed vs base)");
            return _moves = arr.ToString(Newtonsoft.Json.Formatting.None);
        }
    }

    /// <summary>Flatten a CUE4Parse property value to JSON-friendly data (text, enum names and soft paths become strings).</summary>
    static object Simplify(object? v) => v switch
    {
        null => "",
        bool or int or long or float or double or string or byte or short or ushort or uint => v,
        CUE4Parse.UE4.Objects.UObject.FName n => n.Text,
        CUE4Parse.UE4.Objects.Core.i18N.FText t => t.Text ?? "",
        CUE4Parse.UE4.Objects.UObject.FSoftObjectPath sp => sp.AssetPathName.Text,
        _ => v.ToString() ?? "",
    };

    public void Dispose() => _provider.Dispose();
}
