# Connect MailStudio to your AI agent (MCP)

`MailStudio.Mcp.exe` is a [Model Context Protocol](https://modelcontextprotocol.io) server (stdio) that uses the same
accounts, local cache and sign-in tokens as MailStudio. Your agent can search and read your mail, **read attachments
(drawings, STEP models, Excel…)** and use your **skills**. It **never sends mail** – there is no send tool at any
permission level.

Release package: `mcp\MailStudio.Mcp.exe` next to `MailStudio.exe`.
Development build: `src\MailStudio.Mcp\bin\Debug\net8.0\MailStudio.Mcp.exe`.

## One click: AI → Connect agents

MailStudio finds the agents installed on the computer and writes the `mailstudio` entry into their own config file
(a `.mailstudio-backup` copy is made first; nothing else in the file changes). **Remove** takes it out again,
**Test** starts the server like the agent would and lists its tools.

| Agent | Config file |
|---|---|
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` (Store app: `…\Packages\Claude_*\LocalCache\Roaming\Claude\…`) |
| Claude Code | `%USERPROFILE%\.claude.json` |
| OpenAI Codex | `%USERPROFILE%\.codex\config.toml` |
| Gemini CLI | `%USERPROFILE%\.gemini\settings.json` |
| Cursor | `%USERPROFILE%\.cursor\mcp.json` |
| VS Code (Copilot agent mode) | `%APPDATA%\Code\User\mcp.json` |
| Windsurf | `%USERPROFILE%\.codeium\windsurf\mcp_config.json` |
| LM Studio | `%USERPROFILE%\.lmstudio\mcp.json` |
| Goose | `%APPDATA%\Block\goose\config\config.yaml` |

Restart the agent (or open a new session) after connecting. If you move the MailStudio folder, press **Update**.

## Permission per agent

| Level | `--permission` | Tools |
|---|---|---|
| Read | `read` | `list_accounts`, `list_folders`, `list_messages`, `search_messages`, `read_message`, `read_attachment`, `compare_revisions`, `list_skills`, `get_skill`, `list_events`, `find_free_time`, `list_tasks`, `list_quotes`, `list_rules`, `list_followups` |
| Draft (default) | `draft` | + `create_draft` (saved to Drafts, you send it yourself), `add_task`, `add_event` (no invitations are sent), `add_rule` (disabled by default, user enables it), `update_quote_status` |
| Organize | `organize` | + `flag_message`, `mark_read`, `move_message` (never to Deleted Items, never deletes) |

## Tools

| Tool | What it does |
|---|---|
| `list_accounts` | Registered accounts and this connection's permission |
| `list_folders` | Folders of an account with unread counts |
| `list_messages` | Newest mails of a folder (syncs first) |
| `search_messages` | Full-text search in the local cache (subject / sender / body) |
| `read_message` | The whole mail; does **not** change the read state |
| `read_attachment` | Attachment as text: PDF (OCR for scans), STEP sizes/volumes/PMI, DXF/DWG texts and dimensions, Excel cells, Word, PowerPoint, CSV, CNC |
| `compare_revisions` | Compares two revisions (PDF, STEP, DXF, Excel) given as **attachment names** of mails (never an arbitrary file path) and returns the list of changes |
| `list_skills`, `get_skill` | Your [skills](SKILLS.md) – the agent loads the instructions and follows them |
| `list_events` | Appointments and meetings between two dates (recurring items expanded) |
| `find_free_time` | Free slots on working days (08:00–18:00) for a meeting of a given length |
| `list_tasks` | Your task list (open, overdue, completed or all) |
| `list_quotes` | Quotes in the MailStudio Quote Desk (Teklif Masası) |
| `list_rules` | Email filtering rules configured in MailStudio |
| `list_followups` | Email follow-up reminders awaiting replies |
| `create_draft` | Saves a draft to Drafts (never sends) |
| `add_task`, `add_event` | Adds a task or an appointment (draft level; invitations are never sent by an agent) |
| `add_rule` | Adds an email rule – always **disabled**, you review and enable it in MailStudio; empty conditions and skill actions are refused |
| `update_quote_status` | Updates quote status in the Quote Desk (new, in_progress, sent, won, lost) |
| `flag_message`, `mark_read`, `move_message` | Organize level only |

## Manual setup

Any MCP client that can start a stdio server works. The generic entry:

```json
{
  "mcpServers": {
    "mailstudio": {
      "command": "C:\\path\\to\\MailStudio\\mcp\\MailStudio.Mcp.exe",
      "args": ["--permission", "draft"]
    }
  }
}
```

Codex (`~/.codex/config.toml`):

```toml
[mcp_servers.mailstudio]
command = 'C:\path\to\MailStudio\mcp\MailStudio.Mcp.exe'
args = ["--permission", "draft"]
```

## From inside MailStudio

Tick **Mailbox access (MCP)** in the AI pane: Claude Code / Codex / Gemini CLI use the server temporarily for that
question; their persistent configuration is not touched.

For Microsoft / Google OAuth accounts the MCP server cannot open a browser: sign in to the account once in MailStudio first.
