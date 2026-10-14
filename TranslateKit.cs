using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TranslateDemoBrowser;

/// P/Invoke surface of translatekit.dll (core/include/translate_kit/translate_kit.h).
/// All text crosses the boundary as UTF-8.
internal static unsafe class Native
{
    const string Dll = "translatekit";

    [StructLayout(LayoutKind.Sequential)]
    public struct LanguageResult { public fixed byte language[16]; public float confidence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TranslationResult { public IntPtr text; public IntPtr quality_scores; public UIntPtr quality_len; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModelSpec
    {
        public IntPtr source_lang, target_lang, model_path;
        public IntPtr* vocab_paths;
        public UIntPtr vocab_count;
        public IntPtr shortlist_path, config_path;
        public int num_workers;
    }

    [DllImport(Dll)] public static extern IntPtr tk_version();
    [DllImport(Dll)] public static extern IntPtr tk_last_error();
    [DllImport(Dll)] public static extern int tk_init(out IntPtr ctx);
    [DllImport(Dll)] public static extern int tk_detect_language(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text, out LanguageResult result);
    [DllImport(Dll)] public static extern int tk_load_model(IntPtr ctx, ref ModelSpec spec, out IntPtr model);
    [DllImport(Dll)] public static extern int tk_translate(IntPtr model,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string input, int isHtml, out TranslationResult result);
    [DllImport(Dll)] public static extern int tk_translate_batch(IntPtr model, IntPtr[] inputs, UIntPtr count,
        int isHtml, [Out] TranslationResult[] results);
    [DllImport(Dll)] public static extern void tk_translation_result_free(ref TranslationResult result);
    [DllImport(Dll)] public static extern void tk_model_close(IntPtr model);
    [DllImport(Dll)] public static extern void tk_shutdown(IntPtr ctx);

    public static string LastError() => Marshal.PtrToStringUTF8(tk_last_error()) ?? "";
}

/// One installed model folder (model.*.bin + vocab.*.spm + lex.*.bin, optional config.*.yml).
internal sealed record ModelPair(string Source, string Target, string Dir);

/// Owns the translate-kit context. The library is single-threaded per context, so every
/// call runs on one dedicated background thread; callers get Tasks back.
internal sealed class TranslationEngine : IDisposable
{
    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;
    IntPtr _ctx;
    readonly Dictionary<(string, string), IntPtr> _models = new();
    public IReadOnlyList<ModelPair> Pairs { get; }

    /// Translation threads per model. Each worker holds its own model replica (~120 MB
    /// for the Hungarian models), so this trades memory for speed; 4 gets most of the
    /// throughput at about half the memory of 8 (see README). TDB_WORKERS overrides.
    public static readonly int Workers = int.TryParse(Environment.GetEnvironmentVariable("TDB_WORKERS"), out var w) && w > 0
        ? w : Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
    public string Version { get; private set; } = "";

    public TranslationEngine(string modelsRoot)
    {
        Pairs = DiscoverModels(modelsRoot);
        _thread = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) a(); })
        { IsBackground = true, Name = "translate-kit" };
        _thread.Start();
        Run(() =>
        {
            if (Native.tk_init(out _ctx) != 0) throw new InvalidOperationException(Native.LastError());
            Version = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Native.tk_version()) ?? "";
            return 0;
        }).Wait();
    }

    Task<T> Run<T>(Func<T> f, bool force = false)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_queue.IsAddingCompleted) { tcs.SetCanceled(); return tcs.Task; }
        _queue.Add(() =>
        {
            if (_disposing && !force) { tcs.SetCanceled(); return; }
            try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public unsafe Task<(string Lang, float Confidence)> DetectAsync(string text) => Run(() =>
    {
        Native.tk_detect_language(_ctx, text, out var r);
        return (System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)r.language) ?? "und", r.confidence);
    });

    /// Direct pair, or a pivot through English (e.g. hu -> en -> de).
    public List<(string, string)>? Route(string src, string tgt)
    {
        bool Has(string a, string b) => Pairs.Any(p => p.Source == a && p.Target == b);
        if (src == tgt) return new();
        if (Has(src, tgt)) return new() { (src, tgt) };
        if (src != "en" && tgt != "en" && Has(src, "en") && Has("en", tgt)) return new() { (src, "en"), ("en", tgt) };
        return null;
    }

    public IEnumerable<string> TargetsFrom(string src) =>
        Pairs.Select(p => p.Target).Concat(Pairs.Select(p => p.Source)).Distinct()
             .Where(t => t != src && Route(src, t) != null).OrderBy(t => t);

    /// Translates one HTML fragment along the route. Must be called with a route from Route().
    public Task<string> TranslateAsync(string input, bool isHtml, List<(string, string)> route) => Run(() =>
    {
        string text = input;
        foreach (var (s, t) in route)
        {
            var model = LoadModel(s, t);
            if (Native.tk_translate(model, text, isHtml ? 1 : 0, out var r) != 0)
                throw new InvalidOperationException(Native.LastError());
            text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(r.text) ?? "";
            Native.tk_translation_result_free(ref r);
        }
        return text;
    });

    /// Translates many fragments in one engine call (batched across sentences and
    /// spread over the model's worker threads). Output order matches input order.
    public Task<List<string>> TranslateBatchAsync(IReadOnlyList<string> inputs, bool isHtml, List<(string, string)> route) => Run(() =>
    {
        var texts = inputs.ToList();
        foreach (var (s, t) in route)
        {
            var model = LoadModel(s, t);
            var ptrs = texts.Select(x => System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(x)).ToArray();
            var results = new Native.TranslationResult[ptrs.Length];
            try
            {
                if (Native.tk_translate_batch(model, ptrs, (UIntPtr)ptrs.Length, isHtml ? 1 : 0, results) != 0)
                    throw new InvalidOperationException(Native.LastError());
                for (int i = 0; i < results.Length; i++)
                {
                    texts[i] = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(results[i].text) ?? "";
                    Native.tk_translation_result_free(ref results[i]);
                }
            }
            finally { foreach (var p in ptrs) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p); }
        }
        return texts;
    });

    /// Loads (and caches) the models for a route ahead of time.
    public Task WarmUpAsync(List<(string, string)> route) => Run(() => { foreach (var (s, t) in route) LoadModel(s, t); return 0; });

    unsafe IntPtr LoadModel(string src, string tgt)
    {
        if (_models.TryGetValue((src, tgt), out var m)) return m;
        var pair = Pairs.First(p => p.Source == src && p.Target == tgt);
        string? Find(string prefix, string suffix) =>
            Directory.EnumerateFiles(pair.Dir).Select(Path.GetFileName)
                .FirstOrDefault(f => f!.StartsWith(prefix) && f.EndsWith(suffix)) is { } f ? Path.Combine(pair.Dir, f) : null;

        var owned = new List<IntPtr>();
        IntPtr U(string? s) { var p = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(s ?? ""); owned.Add(p); return p; }
        try
        {
            IntPtr vocab = U(Find("vocab.", ".spm"));
            var config = Find("config.", ".yml");
            var spec = new Native.ModelSpec
            {
                source_lang = U(src), target_lang = U(tgt),
                model_path = U(Find("model.", ".bin")),
                vocab_paths = &vocab, vocab_count = 1,
                shortlist_path = U(Find("lex.", ".bin")),
                config_path = config == null ? IntPtr.Zero : U(config),
                num_workers = Workers,
            };
            if (Native.tk_load_model(_ctx, ref spec, out m) != 0)
                throw new InvalidOperationException($"loading {src}->{tgt}: {Native.LastError()}");
        }
        finally { owned.ForEach(System.Runtime.InteropServices.Marshal.FreeCoTaskMem); }
        _models[(src, tgt)] = m;
        return m;
    }

    static List<ModelPair> DiscoverModels(string root)
    {
        var pairs = new List<ModelPair>();
        if (!Directory.Exists(root)) return pairs;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var files = Directory.EnumerateFiles(dir).Select(Path.GetFileName).ToList();
            var model = files.FirstOrDefault(f => f!.StartsWith("model.") && f.EndsWith(".bin"));
            if (model == null || !files.Any(f => f!.StartsWith("vocab.")) || !files.Any(f => f!.StartsWith("lex."))) continue;
            // "model.enhu.intgemm.alphas.bin" or a folder named like "ende.student.tiny11"
            var m = System.Text.RegularExpressions.Regex.Match(model, @"^model\.([a-z]{2})([a-z]{2})\.");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(dir), @"^([a-z]{2})([a-z]{2})\b");
            if (!m.Success) continue;
            var (s, t) = (m.Groups[1].Value, m.Groups[2].Value);
            if (!pairs.Any(p => p.Source == s && p.Target == t)) pairs.Add(new ModelPair(s, t, dir));
        }
        return pairs;
    }

    public void Dispose()
    {
        // Skip queued work that hasn't started, let the call in flight finish, then close
        // the models (which joins the engine's worker threads) before the process exits.
        // Exiting with worker threads still mid-batch is not a clean shutdown.
        _disposing = true;
        Run(() =>
        {
            foreach (var m in _models.Values) Native.tk_model_close(m);
            if (_ctx != IntPtr.Zero) Native.tk_shutdown(_ctx);
            return 0;
        }, force: true).Wait(30000);
        _queue.CompleteAdding();
        _thread.Join(5000);
    }

    volatile bool _disposing;
}
