using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace AbsolverViewer.Wpf;

/// <summary>
/// WebView2 host for the three.js move viewer in the repo's <c>viewer/</c> folder. The folder is mapped to
/// https://absolver.viewer/ so the page can fetch its exported animation JSON. Call <see cref="EnsureLoadedAsync"/>
/// the first time the tab is shown (WebView2 init is slow and is skipped for users who never open the tab).
/// </summary>
public partial class AnimationViewerControl : UserControl
{
    const string Host = "absolver.viewer";
    const string LiveHost = "live.absolver.viewer";   // never mapped to a folder: every request is answered from LiveResolver
    bool _started, _ready;
    readonly List<string> _pending = new();

    /// <summary>Optional live data source: (kind, arg) -> UTF-8 JSON bytes, or null for 404. Kinds: "moves", "skeleton", "mesh",
    /// "anim" (arg = game path). Served to the page at https://live.absolver.viewer/&lt;kind&gt;. When set, the page reads
    /// directly from the paks instead of the pre-exported viewer/data folder. Runs off the UI thread.</summary>
    public Func<string, string?, byte[]?>? LiveResolver { get; set; }

    /// <summary>Verbose diagnostic lines; hook to the host app's log panel.</summary>
    public event Action<string>? Log;

    /// <summary>The page's edit panel changed a row value: (moveId, rawPropertyName, newValue). Raised on the UI thread.</summary>
    public event Action<string, string, string, bool>? RowEdited;

    /// <summary>The page's "Retime with the row" checkbox changed: (on, moveId).</summary>
    public event Action<bool, string>? RetimeOptionChanged;

    public AnimationViewerControl() { InitializeComponent(); }

    /// <summary>Finds a viewer/ folder (index.html + viewer.js): $ABSOLVER_VIEWER_DIR, else next to the exe or in a parent (dev checkout). Null = use the copy built into the program.</summary>
    public static string? FindViewerDir()
    {
        var env = Environment.GetEnvironmentVariable("ABSOLVER_VIEWER_DIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "index.html"))) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "viewer");
            if (File.Exists(Path.Combine(c, "index.html")) && File.Exists(Path.Combine(c, "viewer.js"))) return c;
        }
        return null;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            // A viewer/ folder (dev checkout, or one placed next to the exe) wins so the page can be edited without a rebuild;
            // otherwise the copy compiled into this assembly is served, which is what keeps a release to a single exe.
            var dir = FindViewerDir();
            if (dir != null && !File.Exists(Path.Combine(dir, "data", "skeleton.json")))
                Log?.Invoke($"viewer: {dir}/data has no skeleton.json - fine in live mode (read from the paks); run the ueexport animjson export only for the offline fallback");
            var env = await CoreWebView2Environment.CreateAsync(null, Environment.GetEnvironmentVariable("ABSOLVER_VIEWER_DATA") ?? Path.Combine(Path.GetTempPath(), "AbsolverViewerWebView2_" + System.Diagnostics.Process.GetCurrentProcess().ProcessName));
            await Web.EnsureCoreWebView2Async(env);
            if (dir != null) Web.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, dir, CoreWebView2HostResourceAccessKind.Allow);
            else Web.CoreWebView2.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
            if (LiveResolver != null)
            {
                await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync($"window.__liveBase='https://{LiveHost}/';");
                Web.CoreWebView2.AddWebResourceRequestedFilter($"https://{LiveHost}/*", CoreWebView2WebResourceContext.All);
            }
            Web.CoreWebView2.WebResourceRequested += (s, e) =>
            {
                if (new Uri(e.Request.Uri).Host == Host) ServeEmbedded(e);
                else OnLiveRequest(s, e);
            };
            Log?.Invoke(dir != null ? $"viewer: page served from folder {dir}" : "viewer: page served from the copy built into the program");
            Web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                    var r = doc.RootElement;
                    if (r.ValueKind != System.Text.Json.JsonValueKind.Object || !r.TryGetProperty("action", out var a)) return;
                    if (a.GetString() == "rowEdit")
                        RowEdited?.Invoke(r.GetProperty("id").GetString() ?? "", r.GetProperty("field").GetString() ?? "", r.GetProperty("value").GetString() ?? "",
                                          !r.TryGetProperty("retime", out var rt) || rt.ValueKind != System.Text.Json.JsonValueKind.False);
                    else if (a.GetString() == "retimeOption")
                        RetimeOptionChanged?.Invoke(r.GetProperty("on").GetBoolean(), r.GetProperty("id").GetString() ?? "");
                }
                catch (Exception ex) { Log?.Invoke("viewer: bad message from page: " + ex.Message); }
            };
            Web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                Log?.Invoke($"viewer: navigation {(e.IsSuccess ? "ok" : "FAILED " + e.WebErrorStatus)}");
                if (!e.IsSuccess) return;
                _ready = true; StatusText.Visibility = Visibility.Collapsed;
                foreach (var js in _pending) _ = Web.CoreWebView2.ExecuteScriptAsync(js);
                _pending.Clear();
            };
            Web.CoreWebView2.Navigate($"https://{Host}/index.html");
        }
        catch (Exception ex) { Fail($"WebView2 failed: {ex.Message} (is the WebView2 Runtime installed?)"); Log?.Invoke(ex.ToString()); }
    }

    /// <summary>Answers a request for the viewer page's own files (index.html, viewer.js, three.min.js ...) from resources compiled into this assembly.</summary>
    void ServeEmbedded(CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = "index.html";
        var asm = typeof(AnimationViewerControl).Assembly;
        using var stream = asm.GetManifestResourceStream("viewer/" + path);
        if (stream == null)
        {
            if (path != "favicon.ico") Log?.Invoke($"viewer: embedded file not found: {path}");
            e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
            return;
        }
        var ms = new MemoryStream(); stream.CopyTo(ms); ms.Position = 0;   // the response owns its stream, so it can't be the resource stream
        var type = Path.GetExtension(path).ToLowerInvariant() switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".json" => "application/json", ".css" => "text/css", _ => "text/plain; charset=utf-8" };
        e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(ms, 200, "OK", $"Content-Type: {type}\r\nCache-Control: no-store");
    }

    async void OnLiveRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri);
            var kind = uri.AbsolutePath.Trim('/');
            string? arg = null;
            var q = uri.Query.TrimStart('?');
            foreach (var kv in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                if (kv.StartsWith("p=")) arg = Uri.UnescapeDataString(kv[2..]);
            var resolver = LiveResolver!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bytes = await Task.Run(() => { try { return resolver(kind, arg); } catch (Exception ex) { Log?.Invoke($"viewer: live '{kind}' failed: {ex.Message}"); return null; } });
            Log?.Invoke($"viewer: live {kind}{(arg != null ? " " + arg : "")} -> {(bytes == null ? "404" : bytes.Length + " bytes")} in {sw.ElapsedMilliseconds}ms");
            const string headers = "Content-Type: application/json\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *";
            e.Response = bytes == null
                ? Web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", headers)
                : Web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", headers);
        }
        catch (Exception ex) { Log?.Invoke("viewer: live request error: " + ex); }
        finally { deferral.Complete(); }
    }

    /// <summary>Drops the page and reloads it (e.g. after the pak set changed and LiveResolver now points at a new source).</summary>
    public void Reload()
    {
        if (Web.CoreWebView2 != null) Web.CoreWebView2.Reload();
    }

    /// <summary>Pushes one attacks-table row (raw property name -> value) to the page for an in-place re-render, no reload.</summary>
    public async Task SetRowAsync(string moveId, IReadOnlyDictionary<string, string> row)
    {
        await EnsureLoadedAsync();
        Run($"window.viewerApi&&viewerApi.setRow({Q(moveId)},{System.Text.Json.JsonSerializer.Serialize(row)})");
    }

    void Fail(string msg) { StatusText.Text = msg; Log?.Invoke("viewer: " + msg); }

    // The page fires boot() on load and exposes window.viewerApi; calls made before that are queued and replayed.
    void Run(string js)
    {
        if (_ready && Web.CoreWebView2 != null) _ = Web.CoreWebView2.ExecuteScriptAsync(js);
        else _pending.Add(js);
    }

    static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    public async Task PreviewMoveAsync(string moveId) { await EnsureLoadedAsync(); Run($"window.viewerApi&&viewerApi.previewMove({Q(moveId)})"); }
    public async Task PlayChainAsync(IEnumerable<string> moveIds) { await EnsureLoadedAsync(); Run($"window.viewerApi&&viewerApi.playChain({System.Text.Json.JsonSerializer.Serialize(moveIds)})"); }
}
