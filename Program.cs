using CefSharp;
using CefSharp.WinForms;

namespace TranslateDemoBrowser;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TranslateDemoBrowser");
        var settings = new CefSettings
        {
            RootCachePath = data,
            CachePath = Path.Combine(data, "cache"),
            LogSeverity = LogSeverity.Disable,
        };
        Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);

        // Models: TK_MODELS, else ./models in this project (fetch-models.ps1), else the
        // translate-kit checkout next to it.
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\.."));
        var models = Environment.GetEnvironmentVariable("TK_MODELS")
            ?? new[] { Path.Combine(project, "models"), Path.Combine(project, @"..\translate-kit-windows\models") }
                .Select(Path.GetFullPath).FirstOrDefault(Directory.Exists)
            ?? Path.Combine(project, "models");
        using var engine = new TranslationEngine(models);

        string? autoTest = null;
        if (args.Length >= 3 && args[0] == "--autotest") { autoTest = args[2]; args = new[] { args[1] }; }
        var start = args.FirstOrDefault() ?? "https://telex.hu";
        Application.Run(new MainForm(engine, start) { AutoTestOut = autoTest });
        // CefSharp.WinForms shuts CEF down on Application exit; calling Cef.Shutdown() here too trips a Chromium check.
    }
}
