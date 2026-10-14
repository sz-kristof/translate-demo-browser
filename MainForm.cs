using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Json;
using CefSharp;
using CefSharp.WinForms;

namespace TranslateDemoBrowser;

internal sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(247, 247, 245);
    static readonly Color Ink = Color.FromArgb(32, 33, 30);
    static readonly Color Muted = Color.FromArgb(105, 108, 100);
    static readonly Color Line = Color.FromArgb(222, 220, 212);
    static readonly Color Accent = Color.FromArgb(222, 88, 51);     // warm orange
    static readonly Color BarBg = Color.FromArgb(255, 246, 239);
    static readonly Pen LinePen = new(Line);           // shared by the separator lines
    static readonly ToolTip Tips = new();               // one tooltip provider for all buttons

    readonly TranslationEngine _engine;
    readonly string _pageJs;
    readonly ChromiumWebBrowser _browser;
    readonly TextBox _address = new();
    readonly FlatButton _back, _forward, _reload, _translateTool;
    readonly Panel _infobar = new();
    readonly Label _infoText = new();
    readonly ComboBox _targets = new();
    readonly FlatButton _translateBtn, _originalBtn, _closeBar;
    readonly Label _status = new();
    readonly ThinProgress _progress = new();

    string _pageLang = "und";
    CancellationTokenSource? _cts;
    bool _translated;

    public MainForm(TranslationEngine engine, string startUrl)
    {
        _engine = engine;
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("TranslateDemoBrowser.page.js")!)
        using (var r = new StreamReader(s)) _pageJs = r.ReadToEnd();

        Text = "Translate Demo Browser";
        BackColor = Bg;
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 860);
        MinimumSize = new Size(640, 400);

        // ---- toolbar ---------------------------------------------------------------
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Bg, Padding = new Padding(8, 8, 8, 8) };
        toolbar.Paint += (_, e) => e.Graphics.DrawLine(LinePen, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        _back = IconButton("\uE72B", "Back");
        _forward = IconButton("\uE72A", "Forward");
        _reload = IconButton("\uE72C", "Reload");
        _translateTool = new FlatButton { Text = "Translate", Width = 104, Dock = DockStyle.Right, Glyph = "\uE8C1" };
        var addressHost = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.White, Border = Line, Padding = new Padding(12, 7, 12, 0) };
        _address.BorderStyle = BorderStyle.None;
        _address.Dock = DockStyle.Fill;
        _address.Font = new Font("Segoe UI", 10.5f);
        _address.ForeColor = Ink;
        addressHost.Controls.Add(_address);
        var gap = new Panel { Width = 8, Dock = DockStyle.Right, BackColor = Bg };
        toolbar.Controls.Add(addressHost);
        toolbar.Controls.Add(gap);
        toolbar.Controls.Add(_translateTool);
        foreach (var b in new[] { _reload, _forward, _back }) toolbar.Controls.Add(b);

        // ---- translate infobar -----------------------------------------------------
        _infobar.Dock = DockStyle.Top;
        _infobar.Height = 46;
        _infobar.BackColor = BarBg;
        _infobar.Padding = new Padding(14, 8, 10, 8);
        _infobar.Visible = false;
        _infobar.Paint += (_, e) => e.Graphics.DrawLine(LinePen, 0, _infobar.Height - 1, _infobar.Width, _infobar.Height - 1);
        var badge = new Label { Text = "\uE8C1", Font = new Font("Segoe Fluent Icons", 14f), ForeColor = Accent, AutoSize = false, Width = 30, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft };
        _infoText.AutoSize = false; _infoText.Dock = DockStyle.Left; _infoText.Width = 300; _infoText.ForeColor = Ink; _infoText.TextAlign = ContentAlignment.MiddleLeft;
        _targets.DropDownStyle = ComboBoxStyle.DropDownList; _targets.Dock = DockStyle.Left; _targets.Width = 130; _targets.FlatStyle = FlatStyle.Flat;
        _translateBtn = new FlatButton { Text = "Translate", Width = 100, Dock = DockStyle.Left, Primary = true };
        _originalBtn = new FlatButton { Text = "Show original", Width = 120, Dock = DockStyle.Left, Enabled = false };
        _closeBar = IconButton("\uE711", "Close");
        _closeBar.Dock = DockStyle.Right;
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.ForeColor = Muted; _status.TextAlign = ContentAlignment.MiddleRight;
        _status.Padding = new Padding(0, 0, 10, 0);
        _infobar.Controls.Add(_status);
        _infobar.Controls.Add(_closeBar);
        foreach (var c in new Control[] { _originalBtn, Spacer(6), _translateBtn, Spacer(10), _targets, _infoText, badge }) _infobar.Controls.Add(c);

        _progress.Dock = DockStyle.Top;
        _progress.Height = 3;
        _progress.Visible = false;

        // ---- browser ---------------------------------------------------------------
        _browser = new ChromiumWebBrowser(startUrl) { Dock = DockStyle.Fill };
        _browser.PermissionHandler = new DenyAllPermissions();
        Controls.Add(_browser);
        Controls.Add(_progress);
        Controls.Add(_infobar);
        Controls.Add(toolbar);

        _back.Click += (_, _) => { if (_browser.CanGoBack) _browser.Back(); };
        _forward.Click += (_, _) => { if (_browser.CanGoForward) _browser.Forward(); };
        _reload.Click += (_, _) => _browser.Reload();
        _address.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(_address.Text); } };
        _translateTool.Click += (_, _) => { _infobar.Visible = true; _ = Translate(); };
        _translateBtn.Click += (_, _) => _ = Translate();
        _originalBtn.Click += (_, _) => ShowOriginal();
        _closeBar.Click += (_, _) => _infobar.Visible = false;

        _browser.AddressChanged += (_, e) => BeginInvoke(() => _address.Text = e.Address);
        _browser.TitleChanged += (_, e) => BeginInvoke(() => Text = $"{e.Title} — Translate Demo Browser");
        _browser.LoadingStateChanged += (_, e) => BeginInvoke(() =>
        {
            _back.Enabled = e.CanGoBack; _forward.Enabled = e.CanGoForward;
            _reload.Glyph = e.IsLoading ? "\uE711" : "\uE72C";
        });
        _browser.FrameLoadStart += (_, e) => { if (e.Frame.IsMain) BeginInvoke(ResetForNewPage); };
        _browser.FrameLoadEnd += (_, e) => { if (e.Frame.IsMain) BeginInvoke(() => _ = OnPageLoaded()); };
    }

    static Panel Spacer(int w) => new() { Width = w, Dock = DockStyle.Left, BackColor = Color.Transparent };

    static FlatButton IconButton(string glyph, string tip)
    {
        var b = new FlatButton { Glyph = glyph, Width = 36, Dock = DockStyle.Left };
        Tips.SetToolTip(b, tip);
        return b;
    }

    void Navigate(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return;
        string url = Uri.TryCreate(input, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? input
            : input.Contains('.') && !input.Contains(' ') ? "https://" + input
            : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(input);
        _browser.LoadUrl(url);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();   // stop the follow-up loop before Chromium shuts down
        base.OnFormClosing(e);
    }

    void ResetForNewPage()
    {
        _cts?.Cancel();
        _translated = false;
        _originalBtn.Enabled = false;
        _progress.Visible = false;
        _status.Text = "";
    }

    static string LangName(string code)
    {
        try { var c = CultureInfo.GetCultureInfo(code); return c.EnglishName.Split(' ')[0]; }
        catch { return code; }
    }

    async Task OnPageLoaded()
    {
        try
        {
            await _browser.EvaluateScriptAsync(_pageJs);
            var r = await _browser.EvaluateScriptAsync("window.__tk && window.__tk.sample()");
            if (r.Result is not string json) return;
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("title").GetString() + "\n" + doc.RootElement.GetProperty("text").GetString();
            var (lang, conf) = await _engine.DetectAsync(text);
            _pageLang = lang;

            var targets = _engine.TargetsFrom(lang).ToList();
            if (lang == "und" || conf < 0.5f || targets.Count == 0) { _infobar.Visible = false; return; }

            _targets.Items.Clear();
            foreach (var t in targets) _targets.Items.Add(new LangItem(t));
            // Foreign page -> English; English page -> the user's Windows language, else Hungarian.
            // TDB_TARGET forces the target (self-tests, e.g. the hu -> en -> de pivot).
            var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var forced = Environment.GetEnvironmentVariable("TDB_TARGET");
            var preferred = forced != null && targets.Contains(forced) ? forced
                : lang != "en" && targets.Contains("en") ? "en"
                : targets.Contains(ui) ? ui : targets.Contains("hu") ? "hu" : targets[0];
            _targets.SelectedItem = _targets.Items.Cast<LangItem>().First(i => i.Code == preferred);
            _infoText.Text = $"This page is in {LangName(lang)}.  Translate to";
            _infoText.Width = TextRenderer.MeasureText(_infoText.Text, _infoText.Font).Width + 8;
            _translateBtn.Enabled = true;
            _status.Text = $"detected {LangName(lang)} · {conf:P0} · on-device";
            _infobar.Visible = true;
            if (AutoTestOut != null && !_autoStarted) { _autoStarted = true; await RunAutoTest(lang, conf); }
        }
        catch (Exception ex) { Debug.WriteLine(ex); if (AutoTestOut != null) { File.WriteAllText(AutoTestOut + ".txt", ex.ToString()); Close(); } }
    }

    // --autotest <url> <out>: detect, translate, screenshot the window to <out>.png and
    // write a report to <out>.txt, then exit. Used to verify the app end to end.
    public string? AutoTestOut { get; init; }
    bool _autoStarted;

    async Task RunAutoTest(string lang, float conf)
    {
        var before = await _browser.EvaluateScriptAsync("document.body.innerText.slice(0, 400)");
        await Translate();
        await Task.Delay(5000);  // let late content (pop-ups) get translated too
        var after = await _browser.EvaluateScriptAsync("document.body.innerText.slice(0, 30000)");
        // Render the window ourselves (toolbar via DrawToBitmap, page via Chromium's own
        // screenshot), so the capture never includes whatever else is on screen.
        using (var bmp = new Bitmap(Width, Height))
        {
            DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));   // whole window, title bar included
            using var devTools = _browser.GetDevToolsClient();
            var shot = await devTools.Page.CaptureScreenshotAsync(CefSharp.DevTools.Page.CaptureScreenshotFormat.Png);
            using var page = Image.FromStream(new MemoryStream(shot.Data));
            using (var g = Graphics.FromImage(bmp))
            {
                var screen = _browser.RectangleToScreen(_browser.ClientRectangle);
                var dest = new Rectangle(screen.X - Left, screen.Y - Top, screen.Width, screen.Height);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(page, dest);
            }
            bmp.Save(AutoTestOut + ".png", System.Drawing.Imaging.ImageFormat.Png);
        }
        await _browser.EvaluateScriptAsync("window.__tk.restore()");
        var restored = await _browser.EvaluateScriptAsync("document.body.innerText.slice(0, 400)");
        // Live sites change while the test runs (consent dialogs, tickers), so an exact
        // text match is only expected on static pages; also check the language came back.
        var (restoredLang, _) = await _engine.DetectAsync(restored.Result as string ?? "");
        var restoreOk = Equals(restored.Result, before.Result) ? "identical to original"
            : restoredLang == lang ? $"back in {lang} (the page changed during the test, so the text is not identical)"
            : $"FAILED: restored text detected as {restoredLang}";
        File.WriteAllText(AutoTestOut + ".txt", $"restore: {restoreOk}\n" +
            $"timing: load {LastTiming.LoadMs} ms, first screen {LastTiming.FirstScreenMs} ms, total {LastTiming.TotalMs} ms, {LastTiming.Words} words\n" +
            $"detected: {lang} {conf:0.00}\nstatus: {_status.Text}\n\n--- before ---\n{before.Result}\n\n--- restored ---\n{restored.Result}\n\n--- after ---\n{after.Result}\n");
        Close();
    }

    async Task Translate()
    {
        if (_targets.SelectedItem is not LangItem target) return;
        var route = _engine.Route(_pageLang, target.Code);
        if (route == null || route.Count == 0) return;
        if (_translated) ShowOriginal();

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _translateBtn.Enabled = false;
        _progress.Value = 0; _progress.Visible = true;
        var sw = Stopwatch.StartNew();
        try
        {
            _status.Text = route.Count > 1 ? $"loading models ({string.Join(" → ", route.Select(p => p.Item1).Append(target.Code))})…" : "loading model…";
            await _engine.WarmUpAsync(route);
            var loadMs = sw.ElapsedMilliseconds;

            var tStart = sw.ElapsedMilliseconds;
            var (count, words, firstScreenMs) = await TranslatePending(route, cts.Token, showProgress: true);
            LastTiming = (loadMs, firstScreenMs, sw.ElapsedMilliseconds - tStart, words);
            _translated = true;
            _originalBtn.Enabled = true;
            var secs = sw.Elapsed.TotalSeconds;
            _status.Text = $"first screen {firstScreenMs / 1000.0:0.0} s · {words:N0} words in {secs:0.0} s · nothing left this PC";
            _ = FollowNewContent(route, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "translation failed: " + ex.Message; }
        finally
        {
            if (_cts == cts) { _progress.Visible = false; _translateBtn.Enabled = true; }
        }
    }

    /// Collects every not-yet-translated unit on the page and translates it, visible first.
    async Task<(int Count, int Words, long FirstScreenMs)> TranslatePending(List<(string, string)> route, CancellationToken ct, bool showProgress)
    {
        var sw = Stopwatch.StartNew();
        var r = await _browser.EvaluateScriptAsync("window.__tk.collect()");
        if (r.Result is not string json) return (0, 0, 0);
        var units = JsonSerializer.Deserialize<List<Unit>>(json, JsonOpts) ?? new();
        int lastVisible = units.FindLastIndex(u => u.Vis);
        long firstScreenMs = lastVisible < 0 ? 0 : -1;
        int words = units.Sum(u => CountWords(u.Html ?? u.Text ?? "", u.Html != null));

        // Chunks: everything on screen first (one batch, so it appears together and
        // fast), then the rest of the page in larger batches. TDB_SINGLE=1 restores the
        // old one-call-per-block path for before/after measurements.
        var chunks = new List<List<Unit>>();
        if (SingleCallMode) chunks.AddRange(units.Chunk(6).Select(c => c.ToList()));
        else
        {
            int first = Math.Max(lastVisible + 1, 1);
            chunks.Add(units.Take(first).ToList());
            chunks.AddRange(units.Skip(first).Chunk(48).Select(c => c.ToList()));
        }

        int done = 0;
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var results = new List<object>();
            if (SingleCallMode)
            {
                foreach (var u in chunk)
                {
                    var outText = await _engine.TranslateAsync(u.Html ?? u.Text ?? "", u.Html != null, route);
                    results.Add(u.Html != null ? new { id = u.Id, html = outText } : new { id = u.Id, text = outText });
                }
            }
            else
            {
                // one engine call per kind: HTML fragments and plain text runs
                foreach (var group in chunk.GroupBy(u => u.Html != null))
                {
                    var list = group.ToList();
                    var outs = await _engine.TranslateBatchAsync(list.Select(u => u.Html ?? u.Text ?? "").ToList(), group.Key, route);
                    for (int k = 0; k < list.Count; k++)
                        results.Add(group.Key ? new { id = list[k].Id, html = outs[k] } : new { id = list[k].Id, text = outs[k] });
                }
            }
            ct.ThrowIfCancellationRequested();
            await _browser.EvaluateScriptAsync($"window.__tk.apply({JsonSerializer.Serialize(results)})");
            done += chunk.Count;
            if (firstScreenMs < 0 && done > lastVisible) firstScreenMs = sw.ElapsedMilliseconds;
            if (showProgress)
            {
                _progress.Value = (float)done / units.Count;
                _status.Text = $"translating on this device… {done}/{units.Count}";
            }
        }
        return (units.Count, words, Math.Max(firstScreenMs, 0));
    }

    static readonly bool SingleCallMode = Environment.GetEnvironmentVariable("TDB_SINGLE") == "1";

    static readonly System.Text.RegularExpressions.Regex Tags = new("<[^>]*>|&[a-z#0-9]+;", System.Text.RegularExpressions.RegexOptions.Compiled);
    static int CountWords(string s, bool isHtml) =>
        (isHtml ? Tags.Replace(s, " ") : s).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// (model load, first screen, total translate, words) of the last full translation, in ms.
    public (long LoadMs, long FirstScreenMs, long TotalMs, int Words) LastTiming { get; private set; }

    /// Like Chrome: while a page is shown translated, text the site adds later (pop-ups,
    /// infinite scroll, live tickers) is translated too.
    async Task FollowNewContent(List<(string, string)> route, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _translated)
            {
                await Task.Delay(1500, ct);
                // page.js's MutationObserver says whether the site changed anything since
                // the last pass; only then walk the DOM again.
                var changed = await _browser.EvaluateScriptAsync("window.__tk && window.__tk.takeDirty()");
                if (changed.Result is true) await TranslatePending(route, ct, showProgress: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    void ShowOriginal()
    {
        _cts?.Cancel();
        _browser.ExecuteScriptAsync("window.__tk && window.__tk.restore()");
        _translated = false;
        _originalBtn.Enabled = false;
        _status.Text = "showing original";
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    sealed record Unit(string Id, string? Html, string? Text, bool Vis);
    sealed record LangItem(string Code) { public override string ToString() => LangName(Code); }
}

/// Flat button with an optional Segoe Fluent Icons glyph and hover state.
internal sealed class FlatButton : Control
{
    static readonly Font IconFont = new("Segoe Fluent Icons", 11f);   // created once, not per paint
    bool _hover, _down;
    string _glyph = "";
    public bool Primary { get; set; }
    public string Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        Height = 32;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 1, Width - 5, Height - 3);
        Color fill = Primary ? Color.FromArgb(_down ? 190 : _hover ? 205 : 222, 88, 51)
                   : _down ? Color.FromArgb(226, 224, 216) : _hover ? Color.FromArgb(236, 234, 228) : Color.Transparent;
        if (!Enabled) fill = Primary ? Color.FromArgb(232, 190, 176) : Color.Transparent;
        if (fill.A > 0) using (var path = RoundedPanel.Round(r, 8)) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!Primary && !string.IsNullOrEmpty(Text) && Enabled)
            using (var path = RoundedPanel.Round(r, 8)) using (var p = new Pen(Color.FromArgb(214, 211, 202))) g.DrawPath(p, path);

        var fg = Primary ? Color.White : Enabled ? Color.FromArgb(32, 33, 30) : Color.FromArgb(170, 170, 165);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;
        if (string.IsNullOrEmpty(Text))
        {
            TextRenderer.DrawText(g, Glyph, IconFont, r, fg, flags);
        }
        else if (!string.IsNullOrEmpty(Glyph))
        {
            var tw = TextRenderer.MeasureText(Text, Font).Width;
            var x = (Width - tw - 22) / 2;
            TextRenderer.DrawText(g, Glyph, IconFont, new Rectangle(x, r.Y, 20, r.Height), fg, flags);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + 22, r.Y, tw + 2, r.Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else TextRenderer.DrawText(g, Text, Font, r, fg, flags);
    }
}

internal sealed class RoundedPanel : Panel
{
    public Color Border { get; set; } = Color.Silver;
    public RoundedPanel() { SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true); }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Round(r, Height / 2 - 1);
        using var b = new SolidBrush(BackColor);
        using var pen = new Pen(Border);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class ThinProgress : Control
{
    float _value;
    public float Value { get => _value; set { _value = Math.Clamp(value, 0, 1); Invalidate(); } }
    public ThinProgress() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(240, 222, 210));
        using var b = new SolidBrush(Color.FromArgb(222, 88, 51));
        e.Graphics.FillRectangle(b, 0, 0, Width * _value, Height);
    }
}

/// Privacy default: sites get no geolocation, camera, microphone, notifications, etc.
/// (A geolocation request otherwise surfaces the Windows location consent dialog.)
internal sealed class DenyAllPermissions : CefSharp.Handler.PermissionHandler
{
    protected override bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId,
        string requestingOrigin, PermissionRequestType permissionRequestTypes, IPermissionPromptCallback callback)
    {
        using (callback) callback.Continue(PermissionRequestResult.Deny);
        return true;
    }

    protected override bool OnRequestMediaAccessPermission(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        string requestingOrigin, MediaAccessPermissionType requestedPermissions, IMediaAccessCallback callback)
    {
        using (callback) callback.Continue(MediaAccessPermissionType.None);
        return true;
    }
}
