using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace MailStudio.Regression;

sealed class OllamaFixture : HttpMessageHandler
{
    public readonly List<JsonObject> Calls = new();
    public Func<JsonObject, CancellationToken, Task<HttpResponseMessage>>? Generate;
    public bool UnloadWasCancelled;
    public int TagCalls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == "/api/tags")
        {
            TagCalls++;
            return Reply("{\"models\":[{\"name\":\"test-model\"}]}");
        }
        var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        lock (Calls) Calls.Add(payload);
        if (payload["prompt"] == null)
        {
            UnloadWasCancelled = ct.IsCancellationRequested;
            return Reply("{\"done\":true,\"done_reason\":\"unload\"}");
        }
        return Generate == null ? Reply("{\"response\":\"OK\",\"done\":false}\n{\"done\":true}\n") : await Generate(payload,ct);
    }
    public static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body,Encoding.UTF8,"application/x-ndjson") };
}
