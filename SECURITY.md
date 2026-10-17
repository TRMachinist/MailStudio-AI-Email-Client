# Security Policy

MailStudio handles mail accounts and attachments, so security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's
[private vulnerability reporting](../../security/advisories/new) for this repository instead, and include steps to
reproduce. Do not include real credentials or real mail.

## Design notes

- Passwords and OAuth tokens are stored encrypted with Windows DPAPI (current user) in `%AppData%\MailStudio`.
- The repository contains **no OAuth client secrets**. Builds use empty defaults; each user or distributor registers
  their own OAuth app (see [docs/OAUTH_SETUP.md](docs/OAUTH_SETUP.md)).
- Mail HTML is sanitized, scripts are disabled and remote images are blocked by default.
- Office documents are opened read-only with macros disabled; executables from attachments are never started.
- AI providers receive mail content as fenced data; the AI and the MCP server cannot send mail.
