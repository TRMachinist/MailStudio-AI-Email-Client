# MailStudio MCP server

`MailStudio.Mcp.exe` is a [Model Context Protocol](https://modelcontextprotocol.io) server (stdio) that uses the same
accounts, local cache and sign-in tokens as MailStudio. It **never sends mail** — it can only read, search and leave
drafts in the Drafts folder.

Release package: `mcp\MailStudio.Mcp.exe` next to `MailStudio.exe`.
Development build: `src\MailStudio.Mcp\bin\Debug\net8.0\MailStudio.Mcp.exe`.

## Tools

| Tool | What it does |
|---|---|
| `list_accounts` | Registered accounts |
| `list_folders` | Folders of an account with unread counts |
| `list_messages` | Newest mails of a folder (syncs first) |
| `search_messages` | Full-text search in the local cache (subject / sender / body) |
| `read_message` | The whole mail; does **not** change the read state |
| `create_draft` | Saves a draft to Drafts (never sends) |

## From inside MailStudio

Tick **Mailbox access (MCP)** in the AI pane. Claude Code / Codex / Gemini CLI use the server temporarily for that
question; their persistent configuration is not touched.

## Adding it permanently to other tools (optional)

- **Claude Code:** `claude mcp add mailstudio -- "C:\path\to\MailStudio\mcp\MailStudio.Mcp.exe"`
- **Codex** (`~/.codex/config.toml`):
  ```toml
  [mcp_servers.mailstudio]
  command = 'C:\path\to\MailStudio\mcp\MailStudio.Mcp.exe'
  ```
- **Gemini CLI:** `gemini mcp add mailstudio "C:\path\to\MailStudio\mcp\MailStudio.Mcp.exe"`
- **Goose / Claude Desktop:** add the same exe as a stdio server.

For Microsoft / Google OAuth accounts the MCP server cannot open a browser: sign in to the account once in MailStudio first.
