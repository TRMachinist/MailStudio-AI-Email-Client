# Building MailStudio

MailStudio is a WPF / .NET 8 application with a separate Python worker for the technical viewers.

## Prerequisites

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3.12 x64 (only to build the technical worker: STEP, PDF studio, OCR, DXF/DWG)
- WebView2 runtime (preinstalled on Windows 10/11)

## 1. The app

```powershell
dotnet build MailStudio.sln -c Debug
dotnet run --project src\MailStudio -- --demo        # demo mailbox, no account needed
```

Command-line switches:

| Switch | Meaning |
|---|---|
| `--demo` | Opens a sample mailbox with engineering attachments (nothing connects, nothing is sent) |
| `--view <file>` | Opens a single file in the built-in viewer (PDF, STEP, DXF/DWG, Office, CNC…) |
| `--edit <file>` | Opens a Word / Excel / PowerPoint file in the built-in editor |
| `--background` | Starts minimized to the tray |

Language: **File → Dil / Language** (Türkçe / English). The default follows the Windows display language;
`MAILSTUDIO_LANG=en|tr` overrides it.

## 2. The technical worker (Python)

```powershell
python -m venv work\technical-env
work\technical-env\Scripts\python.exe -m pip install -r src\MailStudio.Technical\requirements.lock.txt pyinstaller
work\technical-env\Scripts\python.exe -m PyInstaller --noconfirm --distpath work\technical-dist --workpath work\technical-build tools\packaging\MailStudio.Technical.spec
work\technical-env\Scripts\python.exe tools\packaging\copy-technical-licenses.py work\ThirdPartyNotices
```

During development you can skip freezing: set `MAILSTUDIO_DEV_PYTHON=work\technical-env\Scripts\python.exe` and the app runs
`src\MailStudio.Technical\worker.py` directly.

## 3. Bundled converters (optional, for a self-contained package)

| Component | Why | License | How |
|---|---|---|---|
| **LibreDWG** `dwg2dxf` | open DWG files | GPLv3 (separate program) | download the win64 release from https://github.com/LibreDWG/libredwg/releases into `work\third-party\libredwg` |
| **LibreOffice** (headless) | view/edit Office files without Microsoft Office | MPL-2.0 | `msiexec /a LibreOffice_<ver>_Win_x86-64.msi /qn TARGETDIR=<abs path>\work\third-party\lo-admin`, then `tools\packaging\make-lo-portable.ps1` |

If Microsoft Office is installed it is used instead of LibreOffice; if ODA File Converter is installed it is used for DWG.

## 4. Portable release folder

```powershell
powershell -ExecutionPolicy Bypass -File tools\packaging\build-release.ps1 -Out work\release
```

Zip the folder — it runs on any Windows 10/11 x64 PC without installation. `tools\packaging\MailStudio.iss` is an optional
Inno Setup installer script.

## 5. Tests

See [tests/README.md](../tests/README.md). All tests use synthetic data and local fake IMAP/SMTP servers.

## 6. Screenshots and GIFs

```powershell
MailStudio.exe --demo --showcase work\shots\en        # scripted tour, captures only MailStudio's own windows
python tools\demo\make_gifs.py work\shots\en docs\media
```

`tools\demo\make_demo_files.py` and `make_demo_step.py` regenerate the synthetic demo attachments in `src\MailStudio\Demo`.

## Translations

UI strings are written in Turkish in the source (`L.T("…")`, `L.F("…", args)`), English texts live in
`src/MailStudio.Core/i18n/en.json`. XAML texts are translated at load time by `UiTranslator`; texts inside templates use
`{local:Tr '…'}`. The UI regression suite in English mode fails if any visible UI string is left untranslated.
