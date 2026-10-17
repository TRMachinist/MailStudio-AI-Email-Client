# Tests

All tests use **synthetic data only**: generated mails, generated CAD/PDF fixtures and fake IMAP/SMTP servers listening
on `127.0.0.1`. They never connect to a real account and never send mail. Each run uses its own temporary data folder
(`MAILSTUDIO_DATA_DIR`), so your real accounts, cache and WebView profile are not touched.

| Suite | What it covers |
|---|---|
| `tests/MailStudio.Regression` | Core / protocol: IMAP sync and UIDVALIDITY, attachment downloads, drafts and recovery, send gate (one SMTP message), BCC, conversations, HTML sanitizing, MIME encodings (ISO-8859-3/9, Windows-1254, UTF-8), conditional formatting, AI prompt fencing, Ollama lifecycle |
| `tests/MailStudio.UiRegression` | Real WPF + WebView2: composer, inline reply / pop-out, signatures, AI rewrite preview with number/unit protection, tray behaviour, PDF studio, STEP measuring and stock, Excel/Word/PowerPoint editors, DXF/DWG viewer, Office conversion without Microsoft Office, **English UI audit** |
| `tests/technical_regression.py` | Technical worker: native CAD distances, oriented envelopes, cylinders, AP242 PMI, units, Unicode paths, PDF render/text, OCR |
| `tests/assembly_regression.py`, `tests/measure_session_regression.py` | Assemblies, CSYS, OBB, three-point angle, ordered measurement sessions, STEP stock export |
| `tests/cad2d_regression.py` | DXF records (lines, arcs, text, dimensions, hatches with holes, blocks) and optional DWG round trip |
| `tests/pdf_studio_regression.py` | PDF studio: words, objects, search, annotations and saving to a copy |

```powershell
dotnet run --project tests\MailStudio.Regression
dotnet run --project tests\MailStudio.UiRegression

# English UI (fails if any visible UI string is still Turkish)
$env:MAILSTUDIO_LANG = 'en'; dotnet run --project tests\MailStudio.UiRegression

work\technical-env\Scripts\python.exe tests\technical_regression.py      # prints a fixture folder
work\technical-env\Scripts\python.exe tests\cad2d_regression.py
work\technical-env\Scripts\python.exe tests\pdf_studio_regression.py
```

Optional environment variables for the UI suite:

| Variable | Purpose |
|---|---|
| `MAILSTUDIO_TECHNICAL_EXE` | frozen technical worker to test |
| `MAILSTUDIO_TECHNICAL_FIXTURES` | fixture folder printed by `technical_regression.py` (enables STEP checks) |
| `MAILSTUDIO_PDF_STUDIO_SAMPLE`, `MAILSTUDIO_PPTX_SAMPLE`, `MAILSTUDIO_CAD_SAMPLE`, `MAILSTUDIO_DWG_SAMPLE` | sample files for the PDF studio / PowerPoint / DXF / DWG checks |
| `MAILSTUDIO_FORCE_LIBREOFFICE=1` + `MAILSTUDIO_LIBREOFFICE` + `MAILSTUDIO_LEGACY_SAMPLES` | Office viewing/editing without Microsoft Office |
| `MAILSTUDIO_DWG2DXF` | LibreDWG `dwg2dxf.exe` for DWG checks |

Out of scope: real provider acceptance, OAuth flows, very large mailboxes, Exchange delegation.
