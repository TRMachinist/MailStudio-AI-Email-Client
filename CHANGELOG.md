# Changelog

## 1.8.0 – Revisions, quotes and a safer outbox (2026-10-20)

- **Revision comparison**: "Compare with previous revision" on an attachment (and in the PDF / STEP / CAD viewers) looks for
  earlier revisions of the same part in the mails of the same correspondent (`-revC`, `Rev.D`, `_R02`, `wk44` …) and lets you pick one.
  - **Excel**: rows matched by their first column (an inserted row does not shift the rest), changed / added / removed cells.
  - **STEP**: exact bounding box, volume, surface and body count from the B-Rep, **holes by diameter** (real holes only, not fillets or bosses), PMI.
  - **DXF/DWG**: layers, dimension values and texts (e.g. `8x Ø18` → `8x Ø20`, title block `REV B` → `REV C`).
  - **PDF drawings**: dimensions, tolerances, fits, Ra, title block fields and notes (text layer, or OCR – then marked as such).
  - Nothing is estimated: if a file cannot be read, the window says so. "Add to reply" writes the change list into the answer; "Ask the manufacturing impact" asks the AI.
- **Quote desk** (Ctrl+5): add a request from the mail (right-click) – part, revision, quantity, material and dates are filled in; statuses New / In progress / Sent / Won / Lost, search, filters, Excel export, deadline reminders. Answering the customer with an attachment marks the quote as sent.
- **Mail rules**: from / to / subject / body / attachment / type / account / folder / importance / size conditions; move, Deleted Items, read, flag, category, task, skill actions; write a rule in plain words with AI. Empty conditions are refused (they would match every mail), moves always run last, "Run in folder now" asks first.
- **Follow-up**: "remind me if there is no answer in N days" in the composer; an answer closes it, otherwise a task appears under *Waiting for reply* with a one-click reminder draft in the same thread.
- **Undo send** (0–30 s) and **Send later** (Outbox): the mail waits on disk, SMTP is not contacted until then. Closing MailStudio during the undo time asks; mails that became due while MailStudio was closed are never sent without asking; a send interrupted by a crash is marked *uncertain* and never repeated automatically.
- **Missing attachment warning** (Turkish and English, quoted text and signature ignored), **large attachment** (> 20 MB) ZIP offer.
- **Templates** with `{Ad} {Soyad} {Firma} {Konu} {ParçaNo} {Tarih} {İmza}` (and English names) and **Quick steps** (Ctrl+Shift+1…9).
- **Snooze**: tonight / tomorrow / next week / pick a date; the mail comes back unread with a notification.
- **All inboxes** and **search folders** (Unread, Flagged, This week's attachments, your own).
- **Newsletters**: unsubscribe from the `List-Unsubscribe` header – the web page opens only after you confirm, a mail address opens a draft you send yourself; nothing is ever requested automatically. "Always move this sender to Newsletters".
- **Calendar sync** with Microsoft 365 / Outlook.com, Google Calendar and CalDAV – **off by default** per account. Calendar access is a separate consent asked on the first "Sync now". 60 days back and 400 days ahead are read completely (nothing is deleted from an incomplete listing); only events you put in that account's calendar are uploaded; on a conflict the server wins and your version is kept in the notes.
- **Contact card** (recent mails, attachments, tasks, quotes), **@mentions** in the composer.
- **Agent (MCP) tools**: `list_rules`, `list_followups`, `list_quotes`, `compare_revisions` (attachments only), `add_rule` (added disabled), `update_quote_status`. Still no send tool.
- Self-test: an empty (white/black) screenshot is now a failure – a locked screen can no longer produce a "passed" run without pictures.

## 1.7.1 – Fixes found by the new self-test (2026-10-19)

- Fixed: closing a new mail with "don't save" on a fresh installation raised an error and kept the window open.
- Fixed: the Close button of the address book (and other Cancel/Close buttons of non-dialog windows) did nothing.
- Fixed: the calendar and task views showed the last mail through them (the mail view is now hidden behind them).
- Self-test: `MailStudio.exe --demo --selftest <folder>` and `tools\selftest\Run-SelfTest.ps1` drive the real app on the demo mailbox and write a log with real screenshots.

## 1.7.0 – Calendar and tasks (2026-10-19)

- **Calendar** (bottom bar or Ctrl+2): day, work week, week and month views, date navigator and upcoming list,
  drag to move or resize, double-click or select a range to create, all-day and multi-day items, colour categories,
  busy / tentative / free / out of office, conflict warnings.
- Recurring appointments: daily, every weekday, weekly on chosen days, every 2 weeks, monthly by date or
  "2nd Tuesday" / "last Friday", yearly; end date; edit or delete one occurrence or the whole series.
- **Meetings**: invitations in mail (Outlook, Exchange, Google, Teams/Zoom) show *Accept / Tentative / Decline*
  in the reading pane; updates and cancellations are recognised. Send invitations, updates and cancellations –
  the mail opens as a draft and you send it. Time zones (IANA, Windows and Outlook names) are converted.
- **Reminders** window with snooze and dismiss, for appointments and tasks (also as a tray balloon).
- **Tasks** (Ctrl+4): quick add with "today / tomorrow / weekday / 25.10", due-date groups, overdue, completed,
  priority, category, notes, reminder; flagged mails appear in the list; "Add as task" on any mail (Ctrl+Shift+K),
  "Schedule meeting" from a mail, and "Add to tasks" on AI answers (e.g. after *Extract tasks*).
- Import / export **.ics** (Outlook, Google Calendar).
- Agents (MCP): `list_events`, `find_free_time`, `list_tasks`; with the draft level `add_task` and `add_event`.
  An agent still cannot send anything.
- Calendar and tasks are stored only on this computer (`calendar.json`, `tasks.json` in the data folder).
- Start option `--open calendar|tasks`.
- Saving settings, drafts, contacts and the calendar retries for a moment when antivirus or indexing briefly locks a freshly written file ("Access to the path is denied").

## 1.6.2 – Signature images and cache repair (2026-10-19)

- Fixed: images in Outlook/Exchange signatures (logos, banners) showed as empty boxes in replies and forwards.
  On Turkish Windows the case-insensitive match failed for `data:IMAGE/PNG` (dotless ı); all case-insensitive
  matching is now culture-invariant.
- Quoted mails keep their own formatting: simple `<style>` rules (e.g. `p.MsoNormal`, Wingdings symbols) are applied inline.
- Local cache: a damaged page found while running no longer breaks reading – the message opens from the server and
  the cache is rebuilt at the next start (the old file is kept as a backup). The MCP server never moves the cache file.

## 1.6.1 – Address book and HTML signatures (2026-10-19)

- **Clickable addresses**: sender, To and Cc in the reading pane and message window are links. Click or right-click:
  copy name and address, copy address only, copy all addresses, new mail, add to contacts / edit, find this person's mails.
- **Address book** (Home → Address Book, Ctrl+Shift+B): saved contacts (name, three e-mails, company, title,
  phone, mobile, notes), contact groups (distribution lists), contacts collected from mail, search and filter.
- Import from Outlook / Gmail CSV (English and Turkish headers, `;` or `,`) and vCard; export as CSV or vCard.
- New mail: one "Select Names" dialog fills To, Cc and Bcc; suggestions while typing include saved contacts, groups and company names.
- **HTML signatures**: paste ready-made HTML signature code, open an .htm/.html file or import Outlook signatures
  (Signatures folder, logos in `_files` embedded, `<style>` rules inlined, windows-1254 files read correctly).
  Remote logos are downloaded and embedded; scripts and forms are removed.
- Contacts are stored only on this computer (`%AppData%\MailStudio\contacts.json`).

## 1.6.0 – Skills and agent connection (2026-10-18)

- **Skills**: SKILL.md rules (Claude/Codex-compatible format) that read the mail and its attachments and produce a note,
  a reply draft, an Excel copy (preview, attachment never changed) or flag/move the mail. Buttons in the AI pane,
  optional automatic run on new mail, Skills window with import/export and "describe it, AI writes the skill".
- Five built-in skills: quotation request analysis, technical drawing check, STEP raw stock & weight,
  delivery schedule update (Excel), flag urgent mails.
- Attachment reader for skills and agents: PDF (OCR fallback), STEP (sizes, B-Rep volumes, face types, PMI),
  DXF/DWG (texts, dimensions), Excel cells with addresses, Word, PowerPoint, CSV, CNC.
- **Connect agents** window: one-click MCP setup for Claude Desktop, Claude Code, Codex, Gemini CLI, Cursor, VS Code,
  Windsurf, LM Studio and Goose (backup of each config file, test button, remove).
- MCP server: `read_attachment`, `list_skills`, `get_skill`; permission levels read / draft / organize
  (`flag_message`, `mark_read`, `move_message`). Still no send tool.

## 1.5.0 – MailStudio for TRMachinist (2026-10-17)

First public open-source release.

- Renamed to **MailStudio for TRMachinist**.
- **English user interface** next to Turkish (File → Dil / Language; follows the Windows language by default).
- **Demo mode** (`MailStudio.exe --demo`): a sample mailbox with synthetic engineering attachments and an offline
  demo AI — no account, no network, nothing is sent.
- AI prompts are bilingual; MCP server messages are in English.
- Excel editor recalculates formulas that were saved without cached values.
- Source cleaned for publication: no built-in OAuth secrets, relative build scripts, English docs and code comments.

## 1.4 – No installation required

- Word / Excel / PowerPoint work **without Microsoft Office** using a bundled, trimmed portable LibreOffice.
- DWG opens with the bundled LibreDWG (ODA File Converter is still used if installed).

## 1.3 – 2D CAD: DXF, DWG, DWF

- Vector DXF/DWG viewer (ezdxf) with layers, layouts, snapping measure, text search; DWFx/XPS viewer.

## 1.2 – Office editors, NX mouse layout

- Excel, Word and PowerPoint editors (ClosedXML, Open XML SDK); NX-style mouse in the STEP viewer; sharper PDFs.

## 1.1 – PDF studio, universal viewer, conditional formatting

- PDF studio with annotations, text correction, OCR and AI; viewers for Office, code, CNC, CSV, ZIP, EML;
  Outlook-style conditional formatting (also created by AI); cache self-repair.

## 1.0 – New interface

- Light/dark themes, compact ribbon, responsive panes, new composer and AI suggestion view.
