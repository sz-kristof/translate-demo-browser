# Translate Demo Browser

A small Windows browser that translates web pages **entirely on your PC**: no page text is sent
anywhere. It is a working end-to-end test of [translate-kit for Windows](https://github.com/sz-kristof/translate-kit/tree/windows-support),
my Windows port of DuckDuckGo's on-device translation library, in a .NET + Chromium (CEF) app.

![Hungarian Wikipedia "Budapest" translated to English on-device](docs/screenshot.png)

<sub>Screenshot: the Hungarian Wikipedia article
[Budapest](https://hu.wikipedia.org/wiki/Budapest) (CC BY-SA 4.0), translated to English in the app.</sub>

## What it does

- Detects the page language and offers *"This page is in Hungarian. Translate to English"*.
- Translates on-device with the same models Firefox uses (Bergamot / Marian, int8).
- Keeps the page intact: links, bold text and footnotes survive (block-level HTML translation).
- Visible text first, then the rest of the page; text the site adds later (pop-ups) is translated too.
- *Show original* restores the page exactly.
- Sites get no location, camera, microphone or notification permissions.

## Speed

Pages go to the engine with `tk_translate_batch` on 8 worker threads: everything on screen in one
batch, then the rest in batches of 48 blocks. Ryzen 9 5900X, compared with one call per block on one
thread (`TDB_SINGLE=1 TDB_WORKERS=1`):

| page | first screen | whole page |
|---|---|---|
| telex.hu (HU → EN, 2.4k words) | 0.86 s → **0.52 s** | 5.1 s → **3.0 s** |
| index.hu (HU → EN, 5k words) | 1.86 s → **0.77 s** | 9.2 s → **5.4 s** |
| hu.wikipedia "Budapest" (HU → EN, 20k words) | 4.90 s → **1.25 s** | 39.3 s → **16.6 s** |
| en.wikipedia "Privacy" (EN → HU, 17k words) | 2.39 s → **0.75 s** | 27.5 s → **10.0 s** |

Running multi-threaded also surfaced a race in the engine (3 crashes in 10 runs), fixed in
translate-kit as patch 0013 (0 crashes in 30 runs after).

## Build and run

Needs Windows 10/11 x64, the .NET 8 SDK and Visual Studio 2022+ with the C++ workload.

```
git clone -b windows-support https://github.com/sz-kristof/translate-kit translate-kit-windows
git clone https://github.com/sz-kristof/translate-demo-browser TranslateDemoBrowser
```

1. Build `translatekit.dll`: see the [Windows section](https://github.com/sz-kristof/translate-kit/tree/windows-support#building-from-source)
   of translate-kit (`scripts/build-windows.ps1`). The project picks it up from
   `../translate-kit-windows/build/windows/` (override with `-p:TranslateKitDll=<path>`).
2. Download models (English ⇄ Hungarian by default, checksums verified):
   `powershell -ExecutionPolicy Bypass -File fetch-models.ps1`
3. Run: `dotnet run -c Release -- https://hu.wikipedia.org/wiki/Budapest`

Self-test (translates the page, saves `<out>.png` and `<out>.txt` with timings, then exits):

```
TranslateDemoBrowser.exe --autotest https://hu.wikipedia.org/wiki/Budapest C:\temp\budapest
```

## How it works

| file | role |
|---|---|
| `TranslateKit.cs` | P/Invoke bindings to `translatekit.dll`; one engine thread (the C API is single-threaded per context, the engine fans out internally) |
| `page.js` | injected into the page: splits it into translation units, swaps translations in and out |
| `MainForm.cs` | browser UI, detection → translate flow, batching, follow-up translation, self-test |

Known: CefSharp 152 logs a non-fatal `Check failed: observers_.empty()` at exit (even on
`about:blank`); the app still exits cleanly.

## License

MIT for this project's code. translate-kit is Apache-2.0, CefSharp and CEF are BSD-licensed, and the
models are Mozilla's (downloaded by you, not included here).
