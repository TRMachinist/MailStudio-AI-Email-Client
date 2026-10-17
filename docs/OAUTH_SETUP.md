# OAuth setup (Microsoft 365 / Outlook.com and Gmail)

Microsoft and Google require a one-time **app registration** before a desktop program may sign users in through the
browser. MailStudio ships **without** built-in client IDs, so you register your own (free, ~5 minutes) and paste the
IDs into **Account settings → OAuth settings**. The same client ID can be used on other computers; each user signs in
on their own computer.

> Gmail does not need OAuth at all: an **app password** works (myaccount.google.com/apppasswords).

---

## 1) Microsoft (Outlook.com, Hotmail, Microsoft 365)

1. Open https://entra.microsoft.com (a personal account works too; the first time it may ask you to create a free directory).
2. **Applications → App registrations → New registration**
   - Name: `MailStudio`
   - Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**
   - Redirect URI: platform **Public client/native (mobile & desktop)**, address `http://localhost`
3. On **Authentication** set **Allow public client flows → Yes** and save.
4. **API permissions → Add a permission → APIs my organization uses → Office 365 Exchange Online → Delegated permissions**:
   - `IMAP.AccessAsUser.All`
   - `SMTP.Send`

   MailStudio also asks for `offline_access`. The requested resources are `https://outlook.office.com/IMAP.AccessAsUser.All`
   and `https://outlook.office.com/SMTP.Send` — these are not the Microsoft Graph `Mail.Read` / `Mail.Send` permissions.
   See [Microsoft's IMAP/SMTP OAuth documentation](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth).
5. Copy the **Application (client) ID** from the **Overview** page.
6. MailStudio → Folder → Add Account → "Sign in with Microsoft" → **OAuth settings** → paste the ID → **Sign In and Save**.

**Microsoft 365 (work account) notes**
- Your company may have disabled user consent for third-party apps; then an "admin approval required" message appears and IT must approve.
- IMAP and "SMTP AUTH" must be enabled for the mailbox. IT can enable them with:
  `Set-CASMailbox -Identity user@company.com -ImapEnabled $true -SmtpClientAuthenticationDisabled $false`

---

## 2) Google (Gmail) — optional

1. https://console.cloud.google.com → new project `MailStudio`
2. **APIs & Services → Library → Gmail API → Enable**
3. **OAuth consent screen**: External, app name MailStudio; add your own Gmail address under **Test users**.
4. **Credentials → Create credentials → OAuth client ID → Desktop app**
5. Paste the **Client ID** and **Client secret** into MailStudio's OAuth settings.
6. Add Account → "Gmail (sign in with Google)" → **Sign In and Save**.

Google apps in testing mode may ask you to sign in again every 7 days; MailStudio opens the browser when needed.

### Building your own release with a built-in Google client

Copy `src/MailStudio.Core/Services/OAuthDefaults.Public.cs` to `OAuthDefaults.Local.cs` in the same folder and fill in
your values. The `.Local.cs` file is git-ignored and, when present, replaces the public (empty) defaults at build time.

---

Tokens are stored under `%AppData%\MailStudio`. The Microsoft cache uses MSAL protected storage; the Google path uses
`FileDataStore` files. Do not copy this folder to move sign-ins to another computer — sign in again there.
