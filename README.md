<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-wordmark-dark.svg">
    <img src="assets/logo-wordmark.svg" alt="JustMd" width="223">
  </picture>
</p>

<p align="center">
  <strong>A Markdown viewer for Windows that only views.</strong><br>
  Open a <code>.md</code> file, read it with GitHub-style formatting, close it. No editor, no account, no telemetry.
</p>

<p align="center">
  <a href="https://github.com/khaled-Umar/JustMd/releases/latest">Download</a> ·
  <a href="https://khaled-umar.github.io/JustMd/">Website</a> ·
  <a href="LICENSE">MIT License</a>
</p>

## Features

- **Tabs in one window.** Each file opens in its own tab. Opening a file while the app is running adds a tab to the existing window instead of starting a second copy.
- **Picks up where you left off.** The tabs from your last session reopen on start, each with its scroll position and outline state. Files you open at start are added as extra tabs.
- **GitHub-like rendering** (Markdig): tables, task lists, footnotes, alerts, emoji and syntax-highlighted code.
- **Copy anything**: select and copy text, or use the one-click **Copy** button on code blocks.
- **Safe links**: web links never open silently. A dialog shows the website and the full address first, and warns about look-alike international names and `user@host` tricks. Links to other local Markdown files open in a new tab.
- **Outline**: a collapsible tree of the headings that you can hide and show again without losing its state.
- **Light, Dark or System theme**, from the header or with <kbd>Ctrl</kbd>+<kbd>T</kbd>.
- **Auto-reload** when the file changes on disk, plus **find**, **zoom** and **print**.
- **Relative local images** resolve next to the open file.
- **Hardened against untrusted documents**: sanitized HTML, a strict Content Security Policy, and no script execution from documents.
- **No telemetry.** The app makes no network requests of its own; the only traffic comes from images a document references by URL.

## Install

1. Download `JustMd-Setup-<version>.exe` (2.8 MB) from the [latest release](https://github.com/khaled-Umar/JustMd/releases/latest).
2. Run it. It installs for the current user by default; you can choose an all-users install instead. The installer is not code-signed yet, so **Windows SmartScreen may warn you**: choose *More info*, then *Run anyway*.
3. Pick the options:
   - **Associate Markdown files (.md, .markdown) with JustMd** (ticked)
   - **Add 'Open with JustMd' to the right-click menu of Markdown files** (ticked). On Windows 11 the entry appears under **Show more options**.
   - **Create a desktop shortcut** (not ticked)

If another app already opens Markdown files, Windows keeps it as the default and may ask you once which app to use. Choose JustMd and *Always*, or set it in Settings > Apps > Default apps.

**Requirements**

- Windows 10 or 11, 64-bit. The app is a single x64 build; on Arm PCs it runs through Windows' x64 emulation.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) **x64** (also on Arm PCs). The installer warns you if it is missing and offers to open the download page. The installer is small because the app is framework-dependent.
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), already present on Windows 11 and most Windows 10 PCs. The installer warns you if it is missing.

## Usage

Open files with the **+** button or <kbd>Ctrl</kbd>+<kbd>O</kbd>, by double-clicking an associated file, from the right-click menu, by dropping files on the window, or by passing paths on the command line. The header holds the outline toggle, the tab strip, the **+** button and the theme toggle.

| Shortcut | Action |
| --- | --- |
| <kbd>Ctrl</kbd>+<kbd>O</kbd> | Open files |
| <kbd>Ctrl</kbd>+<kbd>W</kbd> or <kbd>Ctrl</kbd>+<kbd>F4</kbd> | Close tab |
| <kbd>Ctrl</kbd>+<kbd>Tab</kbd> or <kbd>Ctrl</kbd>+<kbd>PgDn</kbd> | Next tab |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Tab</kbd> or <kbd>Ctrl</kbd>+<kbd>PgUp</kbd> | Previous tab |
| <kbd>Ctrl</kbd>+<kbd>1</kbd> … <kbd>9</kbd> | Go to tab 1–9 |
| <kbd>F5</kbd> or <kbd>Ctrl</kbd>+<kbd>R</kbd> | Reload |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | Find |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>O</kbd> | Show or hide the outline |
| <kbd>Ctrl</kbd>+<kbd>T</kbd> | Switch theme (System → Light → Dark) |
| <kbd>Ctrl</kbd>+<kbd>+</kbd> / <kbd>Ctrl</kbd>+<kbd>-</kbd> | Zoom in / out |
| <kbd>Ctrl</kbd>+<kbd>0</kbd> | Reset zoom |
| <kbd>Ctrl</kbd>+<kbd>P</kbd> | Print |

## Build from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build JustMd.sln -c Release
dotnet test
```

To produce the full package, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and run the build script with PowerShell 7:

```powershell
pwsh ./build.ps1            # options: -Version 1.2.0, -SkipTests, -SkipInstaller
```

It runs the tests, publishes the app to `artifacts/publish`, and writes `artifacts/installer/JustMd-Setup-<version>.exe` with a `.sha256` checksum next to it.

## Project layout

| Path | Contents |
| --- | --- |
| `src/JustMd.Core` | Rendering, link classification, local-image URLs, settings and session, the tab model, the single-instance protocol |
| `src/JustMd` | The WinForms shell (window, WebView2 host, link dialog) and the `web/` viewer page |
| `tests/JustMd.Tests` | Unit tests (228) |
| `installer/` | Inno Setup script |
| `assets/` | Logo, wordmark and app icon |
| `tools/icon/` | Generator for everything in `assets/` plus the site favicons |
| `docs/` | The GitHub Pages site |

To regenerate the logo, icon and favicons after changing the geometry in `tools/icon/generate.mjs`:

```powershell
cd tools/icon; npm install; npm run build
```

## Website (GitHub Pages)

The landing page in `docs/` is plain HTML with no build step. To publish it: **Settings → Pages → Build and deployment → Deploy from a branch**, branch `main`, folder `/docs`.

## Contributing

Bug reports and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) © 2026 Khalid Omar Hanafy. Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
