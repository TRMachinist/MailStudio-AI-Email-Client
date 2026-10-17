// Public default: empty. To ship your own build with a built-in Google OAuth desktop client, add
// OAuthDefaults.Local.cs next to this file (same class, your values). It is git-ignored and, when present,
// this file is excluded from compilation (see MailStudio.Core.csproj).
namespace MailStudio.Services;

static class OAuthDefaults
{
    public const string GoogleClientId = "";
    public const string GoogleClientSecret = "";
}
