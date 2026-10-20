using MailStudio.Models;

namespace MailStudio.Services;

public static class AccountColorHelper
{
    static readonly string[] Palette =
    {
        "#0078D4", // Blue
        "#107C41", // Green
        "#5C2D91", // Purple
        "#D83B01", // Orange
        "#008272", // Teal
        "#B4009E", // Magenta
        "#E81123", // Crimson
        "#498205"  // Lime/Dark Green
    };

    public static string GetAccountColor(AccountConfig acc, int index = 0)
    {
        if (!string.IsNullOrWhiteSpace(acc.Color)) return acc.Color;
        return Palette[Math.Abs(index) % Palette.Length];
    }
}
