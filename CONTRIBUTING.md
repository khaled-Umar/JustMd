# Contributing to JustMd

Thanks for helping. JustMd is deliberately small: a fast, safe, read-only Markdown viewer. Changes that keep it that way are the easiest to accept.

## Reporting bugs

Open an [issue](https://github.com/khaled-Umar/JustMd/issues) with:

- your Windows version, whether the PC is x64 or Arm, and the JustMd version;
- the steps to reproduce, and a small `.md` file that shows the problem if you can;
- what you expected and what happened instead.

Please report security problems (for example, a document that manages to run script or open a link without the warning) privately through the **Report a vulnerability** button on the repository's Security tab, not in a public issue. If that button is not available, open an issue asking for a private contact and leave the details out.

## Proposing features

Open an issue first and describe the use case. JustMd is a viewer by design, so features that turn it into an editor are unlikely to be accepted.

## Making changes

You need Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the installer you also need [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet build JustMd.sln -c Release
dotnet test
pwsh ./build.ps1   # optional, PowerShell 7: tests, publish and installer into artifacts/
```

Before you open a pull request:

- keep the change focused on one thing, and match the style of the surrounding code;
- add or update tests in `tests/JustMd.Tests` for behaviour you change;
- make sure `dotnet build` has no new warnings and `dotnet test` passes;
- never weaken the document hardening (HTML sanitizing, Content Security Policy, the external-link warning) without discussing it in an issue first.

Brand assets in `assets/` and `docs/favicon.*` are generated. Edit `tools/icon/generate.mjs` and run `npm install` then `npm run build` in `tools/icon` instead of editing the outputs by hand.

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
