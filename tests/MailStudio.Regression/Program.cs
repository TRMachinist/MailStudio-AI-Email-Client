using System.Text.RegularExpressions;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Regression;
using MimeKit;
using System.Text;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "MailStudio-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
// Never touch the real %AppData%\MailStudio while testing.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MAILSTUDIO_DATA_DIR"))) Environment.SetEnvironmentVariable("MAILSTUDIO_DATA_DIR", Path.Combine(root, "data"));
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> run) { await run().WaitAsync(TimeSpan.FromSeconds(25)); Console.WriteLine("PASS " + name); passed++; }
Task Sync(Action run) { run(); return Task.CompletedTask; }
MailCache Cache(string name) { var cache = new MailCache(Path.Combine(root, name + ".db")); cache.Init(); return cache; }
AccountConfig Account(FakeImap imap, FakeSmtp? smtp = null) => new()
{
    Id = "synthetic", Email = "owner@example.test", UserName = "synthetic", AuthType = AuthType.Password,
    ProtectedPassword = "", ImapHost = "127.0.0.1", ImapPort = imap.Port, ImapSecurity = SecurityMode.None,
    SmtpHost = "127.0.0.1", SmtpPort = smtp?.Port ?? 1, SmtpSecurity = SecurityMode.None
};
MailService Service(MailCache cache, string name) => new(cache, new OAuthService(() => new AppSettings()),
    new LocalDraftStore(Path.Combine(root, name, "drafts")), new SubmissionStore(Path.Combine(root, name, "submissions")));
MailHeader Header(uint uid = 2, uint epoch = 77, string folder = "INBOX") => new() { AccountId = "synthetic", Folder = folder, Uid = uid, UidValidity = epoch };
ComposeRequest Request() => new() { To = "recipient@example.test", Subject = "Regression", Body = "Synthetic test message" };

await Test("corrupt cache is moved aside and rebuilt empty instead of serving wrong folders",()=>Sync(()=>
{
    var path=Path.Combine(root,"corrupt.db");var cache=new MailCache(path);cache.Init();
    cache.UpsertHeaders(Enumerable.Range(1,400).Select(i=>new MailHeader{AccountId="synthetic",Folder="INBOX",Uid=(uint)i,Subject="ok "+i,Preview=new string((char)(65+i%26),300)}));
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var bytes=File.ReadAllBytes(path);for(int i=4096;i<bytes.Length;i+=97)bytes[i]^=0x5A;File.WriteAllBytes(path,bytes);
    var reopened=new MailCache(path);reopened.Init();
    Check(reopened.RepairedBackup!=null&&File.Exists(reopened.RepairedBackup),"Corrupt cache not preserved as backup");
    Check(reopened.GetHeaders("synthetic","INBOX").Count==0,"Rebuilt cache is not empty");
    var healthy=new MailCache(Path.Combine(root,"healthy.db"));healthy.Init();var again=new MailCache(Path.Combine(root,"healthy.db"));again.Init();Check(again.RepairedBackup==null,"Healthy cache was treated as corrupt");
}));
await Test("AI correction preserves technical numeric tokens and excludes quote instructions",()=>Sync(()=>
{
    var original="25 adet, 20MM pay, 3 eksen ve 20 dakika.";
    Check(DraftRewrite.PreservesNumbers(original,"25 adet; 20MM pay; 3 eksen ve 20 dakika."),"Safe correction rejected");
    Check(!DraftRewrite.PreservesNumbers(original,"25 adet, 20MM pay, 3 eksen ve 30 dakika."),"Changed machining time accepted");
    Check(!DraftRewrite.PreservesTechnicalValues(original,"25 adet, 20CM pay, 3 eksen ve 20 saat."),"Changed units accepted with identical numbers");
    var prompt=DraftRewrite.Prompt("Yazım hatalarını düzelt.",original);Check((prompt.Contains("Yeni bilgi")||prompt.Contains("new information"))&&!prompt.Contains("yanit_baglami"),"Correction has unrelated quote context");
}));
await Test("embedded images resolve escaped CID, Content-Location and CSS without loading remote images",()=>Sync(()=>
{
    var bytes=new byte[]{1,2,3};
    var html="<img src='cid:icon%40example.test'><img src=photo.png><div style=\"background-image:url('cid:icon@example.test')\"></div><img src='https://example.test/photo'>";
    var clean=InlineImages.Replace(html,"icon@example.test","photo.png","image/png",bytes);
    Check(System.Text.RegularExpressions.Regex.Matches(clean,"data:image/png;base64,AQID").Count==3,"Inline reference not resolved");
    Check(clean.Contains("https://example.test/photo")&&InlineImages.HasRemote(clean)&&InlineImages.HasRemote("<img src='//example.test/photo'>"),"Remote image policy not detected");
    Check(!InlineImages.References("<p>No image here</p>","icon@example.test",null),"Unreferenced MIME downloaded");
}));

await Test("legacy Turkish MIME bodies and headers decode without character substitution", () => Sync(() =>
{
    using var mail = Service(Cache("legacy-encoding"), "legacy-encoding");
    foreach (var charset in new[] { "iso-8859-3", "iso-8859-9", "windows-1254", "utf-8" })
    foreach (var quotedPrintable in new[] { false, true })
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(EncodingFixtures.Message(charset, quotedPrintable)));
        using var msg = MimeMessage.Load(stream);
        var content = MailService.BuildContent(msg);
        Check(content.Html.Contains(EncodingFixtures.Text) && content.Text.Contains(EncodingFixtures.Text), $"Body charset failed: {charset}/{quotedPrintable}");
        Check(msg.Subject == "İmalat Şartı" && msg.From.Mailboxes.Single().Name == "Çağrı Işıl", $"Header charset failed: {charset}");
        Check(content.Text.Contains("ACME© 2026; 10¹, º"), "Real copyright/superscript/ordinal symbols changed");
    }
    Check(MailService.FixTurkish("ACME© — © 2026") == "ACME© — © 2026", "Unicode copyright was guessed to be Turkish");
}));

await Test("old cached bodies survive migration and refresh once without losing flags or attachments", () => Sync(() =>
{
    var path = Path.Combine(root, "old-encoding-cache.db"); var cache = new MailCache(path); cache.Init();
    var h = new MailHeader { AccountId="synthetic",Folder="INBOX",Uid=2,Date=DateTimeOffset.Now,Subject="©malat",FromName="Ça»rı",IsRead=true,IsFlagged=true,Preview="Original preview" };
    cache.UpsertHeaders(new[]{h}); cache.SaveBody("synthetic","INBOX",2,new(){Html="<p>parçalar¹n</p>",Text="parçalar¹n"});
    cache.SaveAttachments("synthetic","INBOX",2,new(){new(){FileName="drawing.pdf",Part="2"}});
    using(var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path))
    {
        c.Open();using var cmd=c.CreateCommand();cmd.CommandText="ALTER TABLE messages DROP COLUMN body_encoding_version";cmd.ExecuteNonQuery();
    }
    cache.Init();
    var old=cache.GetBody("synthetic","INBOX",2)!;
    Check(old.NeedsEncodingRefresh && old.Text=="parçalar¹n" && old.Attachments.Single().Part=="2", "Migration discarded old body or attachments");
    var refreshed = new MailHeader { AccountId=h.AccountId,Folder=h.Folder,Uid=h.Uid,Date=h.Date,Subject="İmalat",FromName="Çağrı",IsRead=true,IsFlagged=true };
    cache.UpsertHeaders(new[]{refreshed});
    var header=cache.GetHeader("synthetic","INBOX",2)!;
    Check(header.Subject=="İmalat" && header.FromName=="Çağrı" && header.IsRead && header.IsFlagged && header.Preview=="Original preview", "Fresh envelope or cached flags/preview lost");
    Check(cache.GetBody("synthetic","INBOX",2)!.NeedsEncodingRefresh, "Envelope update falsely marked body as repaired");
    cache.SaveBody("synthetic","INBOX",2,new(){Html="<p>parçaların</p>",Text="parçaların"});
    var fresh=cache.GetBody("synthetic","INBOX",2)!;
    Check(!fresh.NeedsEncodingRefresh && fresh.Text=="parçaların" && fresh.Attachments.Count==1, "Fresh body still invalid or metadata lost");
    Check(cache.Search("synthetic","parçaların").Count==1, "Full text index did not receive decoded body");
}));

await Test("atomic attachment failure and cancellation never publish a partial file", async () =>
{
    var path = Path.Combine(root, "atomic", "a.txt");
    try { await AttachmentFiles.WriteAsync(path, async stream => { await stream.WriteAsync(new byte[] { 1 }); throw new IOException("interrupt"); }); } catch (IOException) { }
    Check(!File.Exists(path) && !Directory.GetFiles(Path.GetDirectoryName(path)!).Any(), "Partial download survived");
    await AttachmentFiles.WriteAsync(path, stream => stream.WriteAsync(new byte[] { 1, 2, 3 }).AsTask());
    try { await AttachmentFiles.WriteAsync(path, _ => Task.CompletedTask, new CancellationToken(true)); } catch (OperationCanceledException) { }
    Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "Existing complete download was damaged");
});
await Test("same-named MIME parts and changed UIDVALIDITY have separate cache paths", () => Sync(() =>
{
    var d = AttachmentFiles.DirectoryFor(root, Header());
    var a = AttachmentFiles.PathFor(d, new() { FileName = "drawing.pdf", Part = "2" });
    var b = AttachmentFiles.PathFor(d, new() { FileName = "drawing.pdf", Part = "3" });
    Check(a != b && Path.GetFileName(a) == "drawing.pdf", "Attachment collision");
    Check(d != AttachmentFiles.DirectoryFor(root, Header(epoch: 78)), "Epoch not in key");
}));
await Test("compose rejects reentry and preserves edits made during saving", () => Sync(() =>
{
    var state = new ComposeState(); state.Changed(); var snapshot = state.Revision;
    Check(state.TryBegin() && !state.TryBegin(), "Duplicate submission allowed");
    state.Changed(); state.Saved(snapshot); Check(state.IsDirty, "Newer changes cleared");
    state.End(); Check(state.TryBegin(), "Gate did not release"); state.End();
}));
await Test("local drafts survive restart including BCC and duplicate attachment paths", () => Sync(() =>
{
    var path = Path.Combine(root, "local-drafts"); var request = Request(); request.Bcc = "hidden@example.test"; request.AttachmentPaths.Add("first"); request.AttachmentPaths.Add("second");
    new LocalDraftStore(path).Save("synthetic", request); request.Subject = "Updated"; new LocalDraftStore(path).Save("synthetic", request);
    var list = new LocalDraftStore(path).List();
    Check(list.Count == 1 && list[0].Request.Subject == "Updated" && list[0].Request.Bcc == request.Bcc && list[0].Request.AttachmentPaths.Count == 2, "Local recovery lost fields");
}));
await Test("server draft roundtrip preserves editable body, signature and quoted sections",async()=>
{
    await using var imap=new FakeImap();using var mail=Service(Cache("structured-draft"),"structured-draft");var request=Request();
    request.BodyHtml="<div class='ms-draft-section'><b>Typed reply</b></div><div class='ms-signature-section'><i>My signature</i></div><div class='ms-quote-section'><p>Original message</p></div>";
    await mail.SaveDraftAsync(Account(imap),request);
    var reopened=await mail.OpenDraftAsync(Account(imap),Header(request.DraftUid!.Value,77,"Drafts"),Path.Combine(root,"structured-attachments"));
    Check(reopened.EditableHtml!.Contains("Typed reply")&&!reopened.EditableHtml.Contains("Original message")&&reopened.SignatureHtml!.Contains("My signature")&&reopened.QuotedHtml!.Contains("Original message"),"Draft sections were mixed into AI editable text");
});
await Test("conversation identity separates equal subjects and links changed subjects", () => Sync(() =>
{
    var messages = new List<MailHeader> {
        new() { AccountId="a", MessageId="root@test", Subject="Quote" },
        new() { AccountId="a", MessageId="unrelated@test", Subject="Quote" },
        new() { AccountId="a", MessageId="reply@test", Subject="Changed", References=new(){"root@test"} },
        new() { AccountId="b", MessageId="reply@test", Subject="Changed", References=new(){"root@test"} }
    };
    var groups = ConversationGroups.Group(messages).ToList();
    Check(groups.Count == 3 && messages[0].ConversationKey == messages[2].ConversationKey, "Conversation grouping wrong");
}));
await Test("Reply-To wins and reply-all removes own and duplicate recipients", () => Sync(() =>
{
    var h = new MailHeader { FromAddress="no-reply@example.test", To="OWNER@example.test, other@example.test" };
    var c = new MailContent { ReplyTo="service@example.test", Cc="other@example.test, SERVICE@example.test, alias@example.test" };
    var (to, cc) = ConversationGroups.ReplyRecipients(h,c,new[]{"owner@example.test","alias@example.test"},true);
    Check(to.Contains("service@example.test") && cc == "other@example.test", "Reply recipient mismatch");
}));
await Test("editor keeps formatting but strips executable content, DOM IDs and data links", () => Sync(() =>
{
    var html = EditorContent.Sanitize("<div id='ms-draft' onclick='alert(1)' contenteditable='true'><b>Keep</b><script>alert(1)</script><img src='data:image/svg+xml;base64,AAAA' onerror='alert(1)'><a href='javascript:alert(1)'>X</a><a href='data:text/html,attack'>Y</a><table><tr><td>Cell</td></tr></table></div>");
    Check(html.Contains("<b>Keep</b>") && html.Contains("<td>Cell</td>") && !html.Contains("onclick") && !html.Contains("onerror") && !html.Contains("javascript:") && !html.Contains("<script") && !html.Contains("id=") && !html.Contains("data:"), html);
}));
await Test("cache migrations preserve bodies, attachment metadata and full text on flag refresh", () => Sync(() =>
{
    var cache = Cache("metadata"); cache.SetUidValidity("synthetic","INBOX",77);
    var h = new MailHeader { AccountId="synthetic",Folder="INBOX",Uid=2,UidValidity=77,MessageId="id@test",Date=DateTimeOffset.Now,Subject="Original",IsDraft=true,ReplyTo="reply@example.test", References=new(){"root@test"} };
    cache.UpsertHeaders(new[]{h}); cache.SaveBody("synthetic","INBOX",2,new(){Text="uniqueBodyWord",Html="<b>body</b>"}); cache.SaveAttachments("synthetic","INBOX",2,new(){new(){FileName="file",Part="2"}});
    cache.UpsertHeaders(new[]{h}); cache.Init(); var loaded=cache.GetHeader("synthetic","INBOX",2)!;
    Check(loaded.UidValidity==77 && loaded.IsDraft && loaded.References.SequenceEqual(h.References) && loaded.ReplyTo==h.ReplyTo, "Header metadata lost");
    Check(cache.Search("synthetic","uniqueBodyWord").Count==1 && cache.GetAttachments("synthetic","INBOX",2).Count==1, "Body search or attachments lost on refresh");
}));
await Test("permanent deletion expunges only the requested UID", async () =>
{
    await using var server = new FakeImap(); using var mail = Service(Cache("delete"),"delete");
    await mail.DeletePermanentlyAsync(Account(server), Header());
    Check(server.Messages.ContainsKey(1) && !server.Messages.ContainsKey(2) && server.Deleted.Contains(1),"Unrelated deleted message was expunged");
    Check(server.Commands.Any(c=>c=="UID EXPUNGE 2") && !server.Commands.Any(c=>c=="EXPUNGE"),"Unscoped expunge issued");
});
await Test("UIDPLUS missing blocks permanent deletion before changing flags", async () =>
{
    await using var server = new FakeImap { UidPlus=false }; using var mail = Service(Cache("no-uidplus"),"no-uidplus");
    try { await mail.DeletePermanentlyAsync(Account(server),Header()); throw new Exception("Expected guard"); } catch (NotSupportedException) { }
    Check(!server.Commands.Any(c=>c.StartsWith("UID STORE")),"Flags changed without safe expunge");
});
await Test("stale UIDVALIDITY blocks a command before changing another message", async () =>
{
    await using var server = new FakeImap { Validity=78 }; using var mail = Service(Cache("stale"),"stale");
    try { await mail.DeletePermanentlyAsync(Account(server),Header()); throw new Exception("Expected stale guard"); } catch (InvalidOperationException) { }
    Check(!server.Commands.Any(c=>c.StartsWith("UID STORE")),"Stale UID acted on new epoch");
});
await Test("normal deletion without Trash never falls through to permanent deletion", async () =>
{
    await using var server = new FakeImap { Trash=false }; using var mail = Service(Cache("no-trash"),"no-trash");
    try { await mail.MoveAsync(Account(server),Header(),null); throw new Exception("Expected missing Trash guard"); } catch (InvalidOperationException) { }
    Check(server.Messages.Count==2 && !server.Commands.Any(c=>c.Contains("EXPUNGE") || c.StartsWith("UID STORE")),"Missing Trash deleted data");
});
await Test("normal move uses selected UID without purging another deleted message", async () =>
{
    await using var server = new FakeImap(); using var mail = Service(Cache("move"),"move");
    await mail.MoveAsync(Account(server),Header(),"Trash");
    Check(server.Messages.ContainsKey(1) && !server.Messages.ContainsKey(2),"Move affected wrong UID");
});
await Test("two draft saves replace only their own previous revision", async () =>
{
    await using var server=new FakeImap(); using var mail=Service(Cache("draft"),"draft"); var a=Account(server); var r=Request();
    await mail.SaveDraftAsync(a,r); r.Subject="Updated"; await mail.SaveDraftAsync(a,r);
    Check(server.Appends==2 && server.Messages.Count(m=>m.Value.Contains(r.DraftId))==1 && server.Messages.ContainsKey(1),"Draft duplication or unrelated deletion");
});
await Test("lost APPEND response is never automatically repeated", async () =>
{
    await using var server=new FakeImap { LoseAppendResponse=true }; using var mail=Service(Cache("lost-append"),"lost-append");
    try { await mail.SaveDraftAsync(Account(server),Request()); throw new Exception("Expected lost response"); } catch (IOException) { } catch (MailKit.Net.Imap.ImapProtocolException) { }
    Check(server.Appends==1,"APPEND replayed");
});
await Test("server draft reopens with BCC, inline images and two different same-named attachments", async () =>
{
    await using var server=new FakeImap(); using var mail=Service(Cache("reopen"),"reopen"); var a=Account(server); var r=Request();
    r.Bcc="hidden@example.test";r.BodyHtml="<p>Inline</p><img src=\"data:image/png;base64,AQIDBA==\">";
    foreach(var name in new[]{"a","b"})
    {
        var dir=Path.Combine(root,"reopen",name);Directory.CreateDirectory(dir);var path=Path.Combine(dir,"same.txt");File.WriteAllText(path,name);r.AttachmentPaths.Add(path);
    }
    await mail.SaveDraftAsync(a,r);
    var reopened=await mail.OpenDraftAsync(a,Header(r.DraftUid!.Value,77,"Drafts"),Path.Combine(root,"reopen","attachments"));
    Check(reopened.Bcc==r.Bcc && reopened.BodyHtml!.Contains("data:image/png;base64,AQIDBA==") && reopened.AttachmentPaths.Count==2,"Draft fields or image lost");
    Check(reopened.AttachmentPaths[0]!=reopened.AttachmentPaths[1] && File.ReadAllText(reopened.AttachmentPaths[0])=="a" && File.ReadAllText(reopened.AttachmentPaths[1])=="b","Same-named draft attachments collided");
});
await Test("SMTP acceptance survives a lost QUIT response and BCC stays envelope-only", async () =>
{
    await using var imap=new FakeImap(); await using var smtp=new FakeSmtp { DropOnQuit=true }; using var mail=Service(Cache("quit"),"quit");
    var r=Request();r.Bcc="hidden@example.test";var result=await mail.SendAsync(Account(imap,smtp),r);
    Check(smtp.Accepted==1 && !string.IsNullOrEmpty(result.SubmissionId),"SMTP success was lost");
    Check(smtp.Recipients.Any(s=>s.Contains("hidden@example.test")) && !smtp.Mime.Contains("Bcc:",StringComparison.OrdinalIgnoreCase),"BCC leaked or not delivered");
    Check(imap.Appends==1,"Sent copy missing");
});
await Test("Sent APPEND failure records pending copy without resubmitting SMTP", async () =>
{
    await using var imap=new FakeImap { FailAppend=true }; await using var smtp=new FakeSmtp(); using var mail=Service(Cache("sent-copy"),"sent-copy");
    var result=await mail.SendAsync(Account(imap,smtp),Request());
    var json=File.ReadAllText(Path.Combine(root,"sent-copy","submissions",result.SubmissionId+".json"));
    Check(smtp.Accepted==1 && imap.Appends==1 && result.Warning!=null && json.Contains("SentCopyPending"),"Post-send failure caused false failure or retry");
});
await Test("lost DATA acceptance response records uncertainty and no automatic resend", async () =>
{
    await using var imap=new FakeImap(); await using var smtp=new FakeSmtp { LoseDataResponse=true }; using var mail=Service(Cache("uncertain"),"uncertain");
    var request = Request();
    try { await mail.SendAsync(Account(imap,smtp),request); throw new Exception("Expected uncertainty"); } catch (SubmissionUncertainException ex) { Console.WriteLine("  " + ex.Message); }
    Check(smtp.Accepted==1 && File.ReadAllText(Directory.GetFiles(Path.Combine(root,"uncertain","submissions"),"*.json").Single()).Contains("Uncertain"),"Missing uncertain record");
    var recovered = new LocalDraftStore(Path.Combine(root,"uncertain","drafts")).List().Single().Request;
    Check(recovered.SubmissionState=="Uncertain", "Recovery lost uncertainty: " + recovered.SubmissionState + "; " + root);
    try { await mail.SendAsync(Account(imap,smtp),recovered); throw new Exception("Expected persistent no-resend guard"); } catch (InvalidOperationException) { }
    Check(smtp.Accepted==1,"Recovered uncertain draft was resent");
});
await Test("Ollama model listing never preloads weights; generation unloads on completion", async () =>
{
    using var fixture=new OllamaFixture();using var http=new System.Net.Http.HttpClient(fixture);
    var provider=new MailStudio.Services.Ai.OllamaProvider(()=>"http://synthetic-ollama",http);
    Check((await provider.GetModelsAsync()).Single()=="test-model" && fixture.Calls.Count==0,"Listing preloaded a model");
    var result=new StringBuilder();await foreach(var chunk in provider.StreamAsync("test prompt",null,default))result.Append(chunk);
    Check(result.ToString()=="OK" && fixture.Calls.Count==2 && fixture.Calls.All(c=>c["keep_alive"]!.GetValue<int>()==0),"Keepalive or explicit unload missing");
    Check(fixture.Calls[1]["prompt"]==null && provider.LastReleaseError==null,"Release did not complete");
});
await Test("Ollama cancellation still unloads using an independent token", async () =>
{
    using var fixture=new OllamaFixture();using var http=new System.Net.Http.HttpClient(fixture);using var cancellation=new CancellationTokenSource();
    var provider=new MailStudio.Services.Ai.OllamaProvider(()=>"http://cancel-test",http);
    try { await foreach(var chunk in provider.StreamAsync("test","test-model",cancellation.Token))cancellation.Cancel(); }catch(OperationCanceledException){}
    Check(fixture.Calls.Count==2 && !fixture.UnloadWasCancelled,"Cancelled generation did not release model");
});
await Test("Ollama PDF page images use the chosen model and retain unload behavior",async()=>
{
    using var fixture=new OllamaFixture();using var http=new System.Net.Http.HttpClient(fixture);var provider=new MailStudio.Services.Ai.OllamaProvider(()=>"http://image-test",http);
    await foreach(var chunk in provider.StreamImageAsync("page 1","vision-model",new[]{"AQID"},default)){}
    Check(fixture.Calls[0]["images"]![0]!.GetValue<string>()=="AQID"&&fixture.Calls[0]["model"]!.GetValue<string>()=="vision-model"&&fixture.Calls.Count==2,"Image payload, selected model or cleanup missing");
});
await Test("Ollama early consumer disposal and failed generation both unload", async () =>
{
    using var fixture=new OllamaFixture();using var http=new System.Net.Http.HttpClient(fixture);
    var provider=new MailStudio.Services.Ai.OllamaProvider(()=>"http://dispose-test",http);
    await foreach(var chunk in provider.StreamAsync("test","test-model",default))break;
    Check(fixture.Calls.Count==2,"Consumer break did not unload");
    fixture.Generate=(_,_)=>Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError){Content=new System.Net.Http.StringContent("synthetic failure")});
    try { await foreach(var chunk in provider.StreamAsync("test","test-model",default)){}throw new Exception("Expected generation error"); }catch(InvalidOperationException){}
    Check(fixture.Calls.Count==4,"Generation failure did not unload");
});
await Test("Ollama shutdown cancels active work and waits for its unload", async () =>
{
    using var fixture=new OllamaFixture();using var http=new System.Net.Http.HttpClient(fixture);var started=new TaskCompletionSource();
    fixture.Generate=async(_,ct)=>{started.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return OllamaFixture.Reply("");};
    var provider=new MailStudio.Services.Ai.OllamaProvider(()=>"http://shutdown-test",http);
    async Task Run(){try{await foreach(var chunk in provider.StreamAsync("test","test-model",default)){} }catch(OperationCanceledException){}}
    var run=Run();await started.Task;await provider.StopAsync();await run;
    Check(fixture.Calls.Count==2 && fixture.Calls[1]["prompt"]==null,"Shutdown completed before release");
});
await Test("Ollama concurrent requests for one model cannot unload each other's active generation", async () =>
{
    using var fixture=new OllamaFixture();using var http=new System.Net.Http.HttpClient(fixture);
    fixture.Generate=async(_,ct)=>{await Task.Delay(75,ct);return OllamaFixture.Reply("{\"response\":\"OK\",\"done\":true}\n");};
    var provider=new MailStudio.Services.Ai.OllamaProvider(()=>"http://concurrency-test",http);
    async Task Run(){await foreach(var chunk in provider.StreamAsync("test","test-model",default)){} }
    await Task.WhenAll(Run(),Run());
    Check(fixture.Calls.Count==4 && fixture.Calls[0]["prompt"]!=null && fixture.Calls[1]["prompt"]==null && fixture.Calls[2]["prompt"]!=null && fixture.Calls[3]["prompt"]==null,"Concurrent release ordering wrong");
});
await Test("Conditional formatting matches Outlook-style conditions, first rule wins, AI JSON is validated", () => Sync(() =>
{
    var now = DateTimeOffset.Now;
    var a = new MailHeader { FromName = "Ahmet Yılmaz", FromAddress = "ahmet@tedarikci.test", Subject = "Teklif — flanş", Date = now, HasAttachments = true };
    var b = new MailHeader { FromName = "Bülten", FromAddress = "news@example.test", Subject = "Haftalık", Date = now.AddDays(-40), IsRead = true };
    var rules = new List<FormatRule>
    {
        new() { Name = "Tedarikçi teklif", Conditions = { new() { Field = "from", Value = "tedarikci;bayi" }, new() { Field = "subject", Value = "TEKLİF" }, new() { Field = "unread" } }, TextColor = "#C42B1C", Bold = true },
        new() { Name = "Eski", Conditions = { new() { Field = "older_days", Value = "30" } }, BackColor = "#EDEBE9", Italic = true },
        new() { Name = "Ekli", Conditions = { new() { Field = "attachment" } }, TextColor = "#0F6CBD" },
    };
    FormatRuleEngine.Apply(rules, new[] { a, b });
    Check(a.FormatRuleName == "Tedarikçi teklif" && a.FormatFore == "#C42B1C" && a.FormatBold && a.HasFormatFore, "first matching rule applied with colour and bold");
    Check(b.FormatRuleName == "Eski" && b.FormatBack == "#EDEBE9" && b.FormatItalic && !b.HasFormatFore, "date rule applies background and italic");
    a.IsRead = true; FormatRuleEngine.Apply(rules, new[] { a });
    Check(a.FormatRuleName == "Ekli" && !a.FormatBold, "reading the message falls through to the next rule");
    rules[2].Enabled = false; FormatRuleEngine.Apply(rules, new[] { a });
    Check(a.FormatRuleName == null && a.FormatFore == null, "disabled rules are ignored and formatting is cleared");
    var ai = FormatRuleEngine.ParseAi("Tamam: ```json\n{\"Name\":\"Acil\",\"Conditions\":[{\"Field\":\"subject\",\"Value\":\"acil;urgent\"},{\"Field\":\"bogus\",\"Value\":\"x\"}],\"TextColor\":\"red\",\"BackColor\":\"#fff4ce\",\"Bold\":true}\n```");
    Check(ai.Conditions.Count == 1 && ai.TextColor == null && ai.BackColor == "#FFF4CE" && ai.Bold && ai.Enabled, "AI rule keeps only known fields and valid colours");
    var threw = false; try { FormatRuleEngine.ParseAi("{\"Conditions\":[{\"Field\":\"nope\"}]}"); } catch (FormatException) { threw = true; }
    Check(threw, "AI rule without known conditions is rejected");
    Check(FormatRuleEngine.AiPrompt("Ahmet'ten gelenleri kırmızı yap").Contains("<tarif>Ahmet'ten gelenleri kırmızı yap</tarif>"), "AI prompt fences the user's description as data");
    var settings = new AppSettings { FormatRules = rules };
    var json = System.Text.Json.JsonSerializer.Serialize(settings);
    var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
    Check(back.FormatRules.Count == 3 && back.FormatRules[0].Conditions.Count == 3 && back.FormatRules[1].BackColor == "#EDEBE9", "rules round-trip through settings JSON");
}));
string RepoRoot() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "skills"))) d = d.Parent; return d?.FullName ?? throw new Exception("repo root not found"); }
await Test("skills: built-in SKILL.md files parse, YAML subset, conditions and attachment filters", () => Sync(() =>
{
    var dirs = Directory.GetDirectories(Path.Combine(RepoRoot(), "skills"));
    Check(dirs.Length >= 5, "built-in skills missing");
    foreach (var d in dirs) { var s = MailStudio.Services.Skills.Skill.Load(d, true); Check(s.Error == null, $"{Path.GetFileName(d)}: {s.Error}"); Check(s.TitleEn.Length > 0 && s.TitleTr.Length > 0 && s.Description.Length > 20, $"{s.Id} titles/description"); }
    var md = "---\nname: test-skill\ndescription: \"Says \\\"hi\\\"\" # comment\nmailstudio:\n  title: Test\n  inputs: [mail, pdf, step]\n  run:\n    - button\n    - new-mail\n  when:   # only quotations\n    subject: \"teklif;RFQ\"\n    attachment: true\n  output: reply\n---\n\nDo it.\n";
    var k = MailStudio.Services.Skills.Skill.Parse(md, "x");
    Check(k.Description == "Says \"hi\"" && k.RunsOnButton && k.RunsOnNewMail && k.Output == MailStudio.Services.Skills.SkillOutput.Reply && k.Instructions == "Do it.", "parsed fields");
    Check(k.When.Count == 2 && k.Accepts("Resim.PDF") && k.Accepts("model.stp") && !k.Accepts("plan.xlsx"), "conditions / accepted files");
    var rfq = new MailHeader { Subject = "RFQ flange", HasAttachments = true }; var other = new MailHeader { Subject = "Newsletter", HasAttachments = true };
    Check(k.AppliesTo(rfq, new[] { "a.pdf" }) && !k.AppliesTo(other, new[] { "a.pdf" }), "when-conditions decide");
    var step = MailStudio.Services.Skills.Skill.Load(Path.Combine(RepoRoot(), "skills", "step-cost-estimate"), true);
    Check(!step.AppliesTo(rfq, new[] { "a.pdf" }) && step.AppliesTo(rfq, new[] { "part.STEP" }), "attachment skills need a matching file");
    foreach (var bad in new[] { "no front matter", "---\ndescription: x\n---\nbody", "---\nname: a\nmailstudio:\n  output: email\n---\n", "---\nname: a\nmailstudio:\n  inputs: [exe]\n---\n", "---\nname: a\nmailstudio:\n  when:\n    bogus: 1\n---\n" })
    { var threw = false; try { MailStudio.Services.Skills.Skill.Parse(bad, "b"); } catch (FormatException) { threw = true; } Check(threw, "invalid skill accepted: " + bad[..Math.Min(30, bad.Length)]); }
    Check(MailStudio.Services.Skills.Skill.Slug("Teklif İsteği Analizi!") == "teklif-i-stegi-analizi" || MailStudio.Services.Skills.Skill.Slug("Teklif isteği analizi") == "teklif-istegi-analizi", "slug");
}));
await Test("skills: prompt fences data, Excel plan previews and applies to a copy only, organize JSON", () => Sync(() =>
{
    var skill = MailStudio.Services.Skills.Skill.Load(Path.Combine(RepoRoot(), "skills", "delivery-schedule-update"), true);
    var prompt = MailStudio.Services.Skills.SkillRunner.BuildPrompt(skill, "<email>Subject: x</email>", new[] { new MailStudio.Services.Skills.SkillAttachment("plan.xlsx", "Week 43!A1=Part no.") });
    Check(prompt.Contains("<skill-instructions>") && prompt.Contains("<attachment name=\"plan.xlsx\">") && prompt.Contains("\"operations\""), "prompt structure");
    var xlsx = Path.Combine(RepoRoot(), "src", "MailStudio", "Demo", "delivery-schedule-wk43.xlsx");
    var text = AttachmentExtractor.Excel(xlsx);
    Check(text.Contains("# Sheet \"Week 43\"") && text.Contains("A4=CA-2201") && text.Contains("(=SUM("), "Excel extraction with addresses and formulas");
    Check(text.Contains("E2=38.5 ") && text.Contains("F2=4620 (=D2*E2)") && text.Contains("G4=2026-10-27"), "Excel values are culture independent");
    var plan = MailStudio.Services.Skills.SkillRunner.ParseExcel("Sure:\n```json\n{\"file\":\"delivery-schedule-wk43.xlsx\",\"summary\":\"s\",\"operations\":[{\"op\":\"set\",\"sheet\":\"Week 43\",\"cell\":\"G4\",\"value\":\"2026-10-30\"},{\"op\":\"set\",\"sheet\":\"Week 43\",\"cell\":\"D4\",\"value\":45},{\"op\":\"fill\",\"sheet\":\"Week 43\",\"cell\":\"A4:H4\",\"color\":\"#FFF2CC\"},{\"op\":\"comment\",\"cell\":\"G4\",\"text\":\"moved\"},{\"op\":\"delete_sheet\",\"cell\":\"A1\"},{\"op\":\"set\",\"cell\":\"ZZZ0\",\"value\":\"x\"}]}\n```");
    var before = File.ReadAllBytes(xlsx);
    var preview = MailStudio.Services.Skills.ExcelEditApplier.Preview(xlsx, plan, out var warnings);
    Check(preview.Count == 4 && warnings.Count == 2 && preview.Any(c => c.Cell == "D4" && c.NumberChanged && c.After == "45"), "preview rows / warnings");
    var target = Path.Combine(root, "plan-copy.xlsx");
    MailStudio.Services.Skills.ExcelEditApplier.Apply(xlsx, plan, target, out _);
    Check(before.SequenceEqual(File.ReadAllBytes(xlsx)), "attachment changed");
    using (var wb = new ClosedXML.Excel.XLWorkbook(target))
    {
        var ws = wb.Worksheet("Week 43");
        Check(ws.Cell("G4").GetString() == "2026-10-30" && ws.Cell("D4").GetDouble() == 45 && ws.Cell("G4").HasComment, "edits written to the copy");
        Check(ws.Cell("F8").HasFormula, "existing formula kept");
    }
    var same = false; try { MailStudio.Services.Skills.ExcelEditApplier.Apply(xlsx, plan, xlsx, out _); } catch (InvalidOperationException) { same = true; }
    Check(same, "overwriting the attachment is refused");
    var org = MailStudio.Services.Skills.SkillRunner.ParseOrganize("{\"flag\":true,\"read\":null,\"move_to\":\"null\",\"reason\":\"urgent\"}");
    Check(org.Flag == true && org.Read == null && org.MoveTo == null && org.Reason == "urgent" && !org.IsEmpty, "organize plan");
}));
await Test("skills: user store import/export, disable and auto-run flags", () => Sync(() =>
{
    var settings = new AppSettings();
    var store = new MailStudio.Services.Skills.SkillStore(settings);
    var saved = store.Save(MailStudio.Services.Skills.Skill.Template("my-check", "My check", "Checks things.", "1. Check.", "mail", "button, new-mail", "note"));
    try
    {
        Check(store.All().Any(s => s.Id == saved.Id && !s.BuiltIn), "saved skill listed");
        store.SetEnabled(saved, false); Check(store.All().First(s => s.Id == saved.Id).Enabled == false && settings.DisabledSkills.Contains(saved.Id), "disable");
        store.SetEnabled(saved, true); store.SetAuto(saved, true); Check(store.IsAuto(store.All().First(s => s.Id == saved.Id)), "auto");
        var zip = Path.Combine(root, "skill.zip"); MailStudio.Services.Skills.SkillStore.ExportZip(saved, zip);
        var imported = store.Import(zip);
        Check(imported.Count == 1 && imported[0].Id != saved.Id && imported[0].Name == "my-check", "import makes a unique copy");
        store.Delete(imported[0]);
    }
    finally { store.Delete(saved); }
}));
await Test("agents: MCP entry added and removed without touching other settings (JSON, VS Code, TOML, YAML)", () => Sync(() =>
{
    AgentConnector.TestRoot = Path.Combine(root, "agents");
    try
    {
        var exe = Path.Combine(root, "MailStudio.Mcp.exe"); File.WriteAllText(exe, "");
        var agents = AgentConnector.Detect(exe);
        var claude = agents.First(a => a.Id == "claude-desktop"); Directory.CreateDirectory(Path.GetDirectoryName(claude.ConfigPath)!);
        File.WriteAllText(claude.ConfigPath, "{\n  // comment\n  \"theme\": \"dark\",\n  \"mcpServers\": { \"other\": { \"command\": \"x.exe\" } },\n}");
        var backup = AgentConnector.Connect(claude, exe, AgentPermission.Read);
        Check(backup != null && File.Exists(backup), "backup written");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(claude.ConfigPath))!;
        Check(json["theme"]!.GetValue<string>() == "dark" && json["mcpServers"]!["other"] != null && json["mcpServers"]!["mailstudio"]!["args"]![1]!.GetValue<string>() == "read", "JSON entry");
        var vs = agents.First(a => a.Id == "vscode"); AgentConnector.Connect(vs, exe, AgentPermission.Draft);
        Check(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(vs.ConfigPath))!["servers"]!["mailstudio"]!["type"]!.GetValue<string>() == "stdio", "VS Code format");
        var codex = agents.First(a => a.Id == "codex"); Directory.CreateDirectory(Path.GetDirectoryName(codex.ConfigPath)!);
        File.WriteAllText(codex.ConfigPath, "model = \"gpt-6\"\n\n[mcp_servers.other]\ncommand = \"o.exe\"\n");
        AgentConnector.Connect(codex, exe, AgentPermission.Organize); AgentConnector.Connect(codex, exe, AgentPermission.Organize);
        var toml = File.ReadAllText(codex.ConfigPath);
        Check(toml.Split("[mcp_servers.mailstudio]").Length == 2 && toml.Contains("[mcp_servers.other]") && toml.Contains("model = \"gpt-6\"") && toml.Contains("\"organize\""), "TOML entry once, others kept");
        var goose = agents.First(a => a.Id == "goose"); Directory.CreateDirectory(Path.GetDirectoryName(goose.ConfigPath)!);
        File.WriteAllText(goose.ConfigPath, "GOOSE_PROVIDER: ollama\nextensions:\n  developer:\n    enabled: true\n    type: builtin\n");
        AgentConnector.Connect(goose, exe, AgentPermission.Draft);
        Check(File.ReadAllText(goose.ConfigPath).Contains("  mailstudio:\n    enabled: true") && File.ReadAllText(goose.ConfigPath).Contains("  developer:"), "YAML entry");
        var again = AgentConnector.Detect(exe);
        Check(again.Where(a => a.Id is "claude-desktop" or "vscode" or "codex" or "goose").All(a => a.Connected && !a.PathOutdated), "detected as connected");
        Check(again.First(a => a.Id == "codex").Permission == AgentPermission.Organize && again.First(a => a.Id == "claude-desktop").Permission == AgentPermission.Read, "permission read back");
        foreach (var a in again.Where(a => a.Connected)) AgentConnector.Disconnect(a);
        Check(!File.ReadAllText(codex.ConfigPath).Contains("mailstudio") && File.ReadAllText(codex.ConfigPath).Contains("[mcp_servers.other]"), "TOML removed, other kept");
        Check(!File.ReadAllText(goose.ConfigPath).Contains("mailstudio") && File.ReadAllText(goose.ConfigPath).Contains("developer"), "YAML removed, other kept");
        Check(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(claude.ConfigPath))!["mcpServers"]!["other"] != null, "JSON other kept");
        Check(AgentConnector.Detect(exe).All(a => !a.Connected), "all disconnected");
    }
    finally { AgentConnector.TestRoot = null; }
}));
await Test("contacts: Outlook/Gmail/Turkish CSV and vCard import, export round trip, groups in suggestions", () => Sync(() =>
{
    var outlook = "First Name,Last Name,E-mail Address,Company,Business Phone,Mobile Phone\r\nAnna,Keller,anna.keller@northwind.example,Northwind,\"+49 1, ext 2\",\r\n\"Tom\",\"Becker\",t.becker@contoso.example,,,+90 555\r\n";
    var a = ContactStore.ParseCsv(outlook);
    Check(a.Count == 2 && a[0].DisplayName == "Anna Keller" && a[0].Phone == "+49 1, ext 2" && a[1].Mobile == "+90 555", "Outlook CSV");
    var tr = "Ad;Soyadı;E-posta Adresi;Şirket\nYasemin;Çelik;yasemin@ornek.test;Örnek Makina\n";
    var b = ContactStore.ParseCsv(tr);
    Check(b.Count == 1 && b[0].LastName == "Çelik" && b[0].Company == "Örnek Makina" && b[0].Email == "yasemin@ornek.test", "Turkish semicolon CSV");
    var gmail = "Name,Given Name,Family Name,E-mail 1 - Value,Phone 1 - Value\nLisa Wong,Lisa,Wong,lisa@fabrikam.example,123\n";
    Check(ContactStore.ParseCsv(gmail).Single().Email == "lisa@fabrikam.example", "Gmail CSV");
    var vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Keller;Anna;;;\r\nFN:Anna Keller\r\nEMAIL;TYPE=INTERNET:anna@x.example\r\nEMAIL:anna2@x.example\r\nTEL;TYPE=CELL:+49 170\r\nORG:North\\, wind;Sales\r\nNOTE:line1\\nline2\r\n folded\r\nEND:VCARD\r\n";
    var v = ContactStore.ParseVCard(vcf).Single();
    Check(v.FirstName == "Anna" && v.LastName == "Keller" && v.Email2 == "anna2@x.example" && v.Mobile == "+49 170" && v.Company == "North, wind" && v.Notes == "line1\nline2folded", "vCard parse");

    var store = new ContactStore(Path.Combine(root, "contacts.json"));
    var csvPath = Path.Combine(root, "c.csv"); File.WriteAllText(csvPath, outlook);
    var r1 = store.Import(csvPath); var r2 = store.Import(csvPath);
    Check(r1 == (2, 0) && r2 == (0, 2) && store.Contacts.Count == 2, "import does not duplicate");
    var trPath = Path.Combine(root, "tr.csv"); System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    File.WriteAllBytes(trPath, System.Text.Encoding.GetEncoding(1254).GetBytes(tr));
    store.Import(trPath);
    Check(store.FindByEmail("YASEMIN@ornek.test")?.LastName == "Çelik", "ANSI (1254) Turkish CSV and case-insensitive lookup");
    store.Save(new ContactGroup { Name = "Kalite", Members = { "Anna Keller <anna.keller@northwind.example>", "qa@contoso.example" } });
    var reloaded = new ContactStore(Path.Combine(root, "contacts.json"));
    Check(reloaded.Contacts.Count == 3 && reloaded.Groups.Single().Members.Count == 2, "saved to disk");
    var vOut = Path.Combine(root, "out.vcf"); reloaded.ExportVCard(vOut);
    Check(ContactStore.ParseVCard(File.ReadAllText(vOut)).Count == 3, "vCard export round trip");
    var cOut = Path.Combine(root, "out.csv"); reloaded.ExportCsv(cOut);
    Check(ContactStore.ParseCsv(File.ReadAllText(cOut)).Any(c => c.Company == "Örnek Makina"), "CSV export round trip");

    var book = new ContactBook(null, reloaded);
    var kal = book.Suggest("kal").FirstOrDefault();
    Check(kal is { IsGroup: true } && kal.Display.Contains("qa@contoso.example") && kal.Display.Contains("anna.keller@northwind.example"), "group suggestion expands to members");
    Check(book.Suggest("örnek").Any(c => c.Saved && c.Address == "yasemin@ornek.test"), "company search");
    reloaded.AddFromMail("Lisa Wong", "lisa@fabrikam.example");
    Check(book.All.Any(c => c.Saved && c.Name == "Lisa Wong"), "store change refreshes the book");
    var split = ContactStore.SplitAddresses("\"Keller, Anna\" <anna@x.example>; tom@y.example, Lisa <lisa@z.example>");
    Check(split.Count == 3 && split[0].Name == "Keller, Anna" && split[1].Address == "tom@y.example", "address field split");
}));
await Test("signatures: Outlook .htm import (1254 charset, <style> inlined, _files logo embedded), pasted HTML sanitized", async () =>
{
    var dir = Path.Combine(root, "Signatures"); var files = Path.Combine(dir, "Firma_files"); Directory.CreateDirectory(files);
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    File.WriteAllBytes(Path.Combine(files, "image001.png"), png);
    File.WriteAllBytes(Path.Combine(root, "secret.png"), png);
    var htm = "<html><head><meta http-equiv=Content-Type content=\"text/html; charset=windows-1254\"><style><!-- /* Font Definitions */ @font-face {font-family:Calibri;} p.MsoNormal, li.MsoNormal {margin:0cm; font-size:11.0pt; mso-fareast-font-family:Calibri;} @page WordSection1 {size:612pt 792pt;} --></style></head>" +
              "<body><div class=WordSection1><p class=MsoNormal>Saygılarımla,<br><b>Yasin Işık</b></p><table cellpadding=0 style='border:none'><tr><td><img width=120 src=\"Firma_files/image001.png\"></td><td style='color:#0F6CBD'>Çelik Makina</td></tr></table>" +
              "<img src=\"../secret.png\"><img src=\"https://logo.example/x.png\"><script>alert(1)</script><p class=MsoNormal><o:p>&nbsp;</o:p></p></div></body></html>";
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    var path = Path.Combine(dir, "Firma.htm"); File.WriteAllBytes(path, System.Text.Encoding.GetEncoding(1254).GetBytes(htm));
    var found = SignatureImport.FindOutlookSignatures(new[] { dir });
    Check(found.Count == 1 && found[0].Name == "Firma", "Outlook signature folder scan");
    var text = SignatureImport.ReadHtmlFile(path);
    Check(text.Contains("Yasin Işık") && text.Contains("Çelik"), "windows-1254 charset honoured");
    var fetched = new List<string>();
    var html = await SignatureImport.PrepareAsync(text, dir, u => { fetched.Add(u); return Task.FromResult<byte[]?>(png); });
    Check(html.Contains("<table") && html.Contains("Çelik Makina") && Regex.IsMatch(html, @"<td[^>]*style=""[^""]*color"), "table and inline colour kept: " + html);
    Check(Regex.Matches(html, "data:image/png;base64,").Count == 2, "local _files logo and remote logo embedded, file outside folder dropped: " + html);
    Check(fetched.SequenceEqual(new[] { "https://logo.example/x.png" }), "remote image fetched once");
    Check(!html.Contains("script") && !html.Contains("secret") && !html.Contains("WordSection1 {"), "script / outside path removed");
    Check(Regex.IsMatch(html, @"<p[^>]*style=""[^""]*margin:\s*0") && html.Contains("11pt"), "MsoNormal style inlined: " + html);
    Check(!Regex.IsMatch(html, @"<p[^>]*>(\s|&nbsp;)*</p>\s*$"), "trailing empty paragraph trimmed");
    var keep = await SignatureImport.PrepareAsync("<p>Hi <img src=\"https://x.example/a.png\"></p>", null, null);
    Check(keep.Contains("https://x.example/a.png"), "without fetch remote image link is kept");
});
await Test("quoted Outlook mail keeps upper-case data: images and its <style> class formatting", () => Sync(() =>
{
    var png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    var html = "<html><head><style><!-- p.MsoNormal {margin:0cm; font-size:11.0pt;} span.leaf {font-family:Wingdings; mso-style-type:personal;} --></style></head><body>" +
               "<p class=MsoNormal><img width=282 height=90 id=\"Resim_x0020_1\" src=\"data:IMAGE/PNG;base64," + png + "\"></p><p class=MsoNormal><span class=leaf>þ</span> not</p></body></html>";
    var clean = EditorContent.Sanitize(html);
    Check(clean.Contains("data:image/png;base64," + png), "upper-case data image kept: " + clean);
    Check(Regex.IsMatch(clean, @"<p[^>]*style=""[^""]*margin:\s*0") && clean.Contains("Wingdings"), "class styles inlined: " + clean);
    Check(InlineImages.Replace("<img src=\"cid:a@b\">", "a@b", null, "IMAGE/PNG", new byte[] { 1 }).Contains("data:image/png;"), "inline image mime lower-cased");
}));
Console.WriteLine($"RESULT {passed} passed. Synthetic evidence: {root}");
