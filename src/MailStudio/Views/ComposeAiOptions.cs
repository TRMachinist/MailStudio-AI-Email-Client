using MailStudio.Services.Ai;

namespace MailStudio.Views;

public sealed record ComposeAiOptions(IReadOnlyList<IAiProvider> Providers, string Provider, string? Model);
