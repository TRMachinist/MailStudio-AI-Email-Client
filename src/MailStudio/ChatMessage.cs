using CommunityToolkit.Mvvm.ComponentModel;

namespace MailStudio;

public partial class ChatMessage : ObservableObject
{
    public bool IsUser { get; init; }
    public string Author { get; init; } = "";
    public static string CopyLabel => L.T("Kopyala");
    public static string UseAsReplyLabel => L.T("Yanıt olarak aç");
    [ObservableProperty] private string text = "";
    [ObservableProperty] private bool isError;
    [ObservableProperty] private bool showActions;
}
