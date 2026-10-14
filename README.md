# WinDeepSearch

Search inside every file under a folder, straight from the Windows 11 right-click menu.

- Right-click a file, folder, or empty space in a folder: **WinDeepSearch** is in the Windows 11 menu and under
  **Show more options**. On a drive icon in *This PC* it is under Show more options.
- Type text or a regular expression, press **Enter**. `.*` turns regular expressions on (full .NET syntax, including
  lookahead and lookbehind), `Aa` makes it case-sensitive, and the picture button reads text in images.
- Results: file-name matches first, then files by number of matches. Hover a result to see the lines around the match
  (and the picture, for text read from an image); click it to open File Explorer with the file selected. The arrow lists
  every matching line of a file.
- The right panel shows files that take a while, with a progress bar and a **Skip** button.
- Started from a file, the search covers its folder; tick **Just this selected file** to search only that file.
- Searches plain text in any encoding, Word, Excel, PowerPoint (and the older .doc, .xls, .ppt), OpenDocument, PDF,
  and text in pictures, scanned PDFs and pictures inside Office files (OCR with PaddleOCR). Text read from pictures is
  remembered, so searching the same files again is fast.
- Open **WinDeepSearch** from the Start menu to search any folder (C:\ by default), go back to past searches and their
  results in **History**, and change ignored folders, preview lines and history size in **Settings**.

## Limits

- Up to 5,000 matching lines are kept per file, and 200,000 per search; past that the count is still exact and the
  list says that later lines were not kept.
- Large text files are read in 4-million-character chunks; a single match longer than about 64,000 characters that
  crosses a chunk boundary is not found.
- Excel binary workbooks (.xlsb), Outlook messages, Visio, Publisher and OneNote files are read with the filters that
  Microsoft Office installs; without them they are counted as "couldn't be read".
- OneDrive files that are only online are not downloaded; they are listed as skipped.
- Archives (.zip and similar) are not opened. WinDeepSearch never searches its own data folder.
- If File Explorer already shows the folder of a clicked result, Windows selects the file in that window instead of
  opening a new one.

## Install

Download `WinDeepSearch-Setup.exe` from the [latest release](https://github.com/lben/WinDeepSearch/releases/latest)
and run it. It installs for your account only, in `%LOCALAPPDATA%\Programs\WinDeepSearch`. The installer is not
code-signed, so Windows SmartScreen may warn about it: choose **More info › Run anyway**.

Windows asks once for administrator approval: the Windows 11 menu only accepts entries from signed packages, so the
installer adds the package's certificate to the computer's *Trusted People*. If you decline, the entry still appears
under **Show more options**. Uninstall from **Settings › Apps**.

## Build

Windows 11 with the .NET 10 SDK, Visual Studio 2022 C++ tools (or Build Tools), CMake and Inno Setup 6:

```powershell
dotnet test --project tests/WinDeepSearch.Core.Tests
cmake -S src/WinDeepSearch.ShellExt -B build/shellext -A x64; cmake --build build/shellext --config Release
dotnet publish src/WinDeepSearch -c Release -o artifacts/app
Copy-Item build/shellext/Release/WinDeepSearch.ShellExt.dll artifacts/app/
./packaging/build-package.ps1 -AppDir artifacts/app
iscc packaging/WinDeepSearch.iss
```

The engine and its tests also build and run on macOS (`dotnet test --project tests/WinDeepSearch.Core.Tests`); the
Windows-only tests are skipped there.
`.github/workflows/build.yml` builds the installer on Windows, installs it, runs the UI tests against the installed app
(including the Explorer menus) and keeps screenshots. Pushing a tag such as `v0.2.0` also publishes the installer as
release 0.2.0 once all of that passes.

## Layout

- `src/WinDeepSearch.Core` – the search engine: file walking, text detection, document readers, OCR, history.
- `src/WinDeepSearch` – the Windows app (WPF).
- `src/WinDeepSearch.ShellExt` – the Windows 11 menu command (C++ COM DLL).
- `packaging` – the menu package manifest, signing script and installer script.
- `tests` – engine tests and UI tests.

## License

MIT, see [LICENSE](LICENSE). The installer also contains third-party components under their own licenses, listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
