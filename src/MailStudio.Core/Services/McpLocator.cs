namespace MailStudio.Services;

/// <summary>Finds MailStudio.Mcp.exe (release folder or development output).</summary>
public static class McpLocator
{
    public static string? Find()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "mcp", "MailStudio.Mcp.exe"),
            Path.Combine(baseDir, "MailStudio.Mcp.exe"),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\MailStudio.Mcp\bin\Release\net8.0\MailStudio.Mcp.exe")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\MailStudio.Mcp\bin\Debug\net8.0\MailStudio.Mcp.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
