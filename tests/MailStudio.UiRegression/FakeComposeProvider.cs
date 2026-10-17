using System.Runtime.CompilerServices;
using MailStudio.Services.Ai;

namespace MailStudio.Regression;
sealed class FakeComposeProvider(string name) : IAiProvider
{
    public string Name=>name;public string Badge=>"Synthetic";public bool SupportsMcp=>false;
    public string Response="25 adet için 20MM pay ve 3 eksende 20 dakika.";
    public string? LastModel,LastPrompt;
    public Task<IReadOnlyList<string>> GetModelsAsync()=>Task.FromResult<IReadOnlyList<string>>(new[]{"model-a","model-b"});
    public async IAsyncEnumerable<string> StreamAsync(string prompt,string? model,[EnumeratorCancellation] CancellationToken ct,string? mcpServerExe=null)
    {LastModel=model;LastPrompt=prompt;await Task.Delay(30,ct);yield return Response;}
}
