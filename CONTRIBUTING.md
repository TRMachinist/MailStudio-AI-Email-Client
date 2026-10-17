# Contributing to MailStudio

Thanks for helping! Bug reports, feature ideas, translations and pull requests are all welcome.

## Reporting bugs

- Open an issue with steps to reproduce, the MailStudio version and your Windows version.
- **Never attach real mail, account names or customer drawings.** If a file triggers the bug, try to reproduce it
  with a sample (for example the files in `src/MailStudio/Demo`) or remove sensitive content first.

## Development

See [docs/BUILDING.md](docs/BUILDING.md). In short:

```powershell
dotnet build MailStudio.sln
dotnet run --project src\MailStudio -- --demo
```

Always test with the demo mode or a separate data folder (`$env:MAILSTUDIO_DATA_DIR = "$env:TEMP\ms-dev"`) so your own
mailbox is never touched.

Before a pull request, run the tests described in [tests/README.md](tests/README.md) — at least
`tests/MailStudio.Regression`, and `tests/MailStudio.UiRegression` for UI changes (in both languages:
`$env:MAILSTUDIO_LANG = "en"` / `"tr"`).

## Coding guidelines

- C# / WPF on .NET 8, nullable enabled. Keep UI code in `src/MailStudio`, logic in `src/MailStudio.Core`.
- Never write to an attachment file; editors and the PDF studio always save to a new file.
- The AI must never be able to send mail.

## Translations

Turkish is the source language of the UI. User-visible strings are written in Turkish and wrapped with
`L.T("…")` / `L.F("… {0}", x)` in C#, or translated automatically in XAML by `UiTranslator`.
The English texts live in [`src/MailStudio.Core/i18n/en.json`](src/MailStudio.Core/i18n/en.json)
(`"Turkish source": "English text"`).

- To fix an English text, edit `en.json`.
- When you add a new Turkish string, add its English entry too; the UI regression test fails in English mode if a
  visible string is untranslated.
- New languages: open an issue first — we will add a language selector entry and a `<lang>.json` loader.
