using System.Text.RegularExpressions;
using MailStudio;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Calendar;
using MailStudio.Regression;
using MimeKit;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

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
    var marked=Path.Combine(root,"marked.db");var m1=new MailCache(marked);m1.Init();m1.UpsertHeaders(new[]{new MailHeader{AccountId="synthetic",Folder="INBOX",Uid=1,Subject="x"}});
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();File.WriteAllText(marked+".repair","x");
    var helper=new MailCache(marked);helper.Init(repair:false);Check(helper.RepairedBackup==null&&helper.GetHeaders("synthetic","INBOX").Count==1,"helper process (MCP) must never move the cache");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var m2=new MailCache(marked);m2.Init();Check(m2.RepairedBackup!=null&&!File.Exists(marked+".repair")&&m2.GetHeaders("synthetic","INBOX").Count==0,"runtime corruption marker rebuilds the cache at next start");
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
await Test("calendar: Outlook/Google invitations (time zones, RRULE, EXDATE, alarm), recurrence, store, reminders, round trip", () => Sync(() =>
{
    var outlook = string.Join("\r\n", new[] {
        "BEGIN:VCALENDAR","METHOD:REQUEST","PRODID:Microsoft Exchange Server 2010","VERSION:2.0",
        "BEGIN:VTIMEZONE","TZID:(UTC+03:00) Istanbul","BEGIN:STANDARD","DTSTART:16010101T000000","TZOFFSETFROM:+0300","TZOFFSETTO:+0300","END:STANDARD","END:VTIMEZONE",
        "BEGIN:VEVENT","ORGANIZER;CN=\"Kalite, Ayşe\":mailto:ayse@ornek.test","ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=Tom Becker:mailto:tom@contoso.example",
        "DESCRIPTION;LANGUAGE=tr-TR:İlk numune\\, FAI ve 3.1 sertifika\\ngörüşmesi","UID:040000008200E00074C5B7101A82E00800000000ABCDEF","SUMMARY;LANGUAGE=tr-TR:NW-1042 ilk numune toplant",
        " ısı","DTSTART;TZID=(UTC+03:00) Istanbul:20261026T100000","DTEND;TZID=(UTC+03:00) Istanbul:20261026T110000",
        "RRULE:FREQ=WEEKLY;UNTIL=20261130T070000Z;INTERVAL=1;BYDAY=MO,WE;WKST=MO","EXDATE;TZID=(UTC+03:00) Istanbul:20261028T100000",
        "LOCATION;LANGUAGE=tr-TR:Toplantı Odası 2","SEQUENCE:2","X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE",
        "BEGIN:VALARM","ACTION:DISPLAY","DESCRIPTION:REMINDER","TRIGGER;RELATED=START:-PT30M","END:VALARM","END:VEVENT","END:VCALENDAR" });
    var cal = MailStudio.Services.Calendar.Ics.Parse(outlook);
    var ev = cal.Events.Single();
    var istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    DateTime Local(int y, int mo, int d, int h) => TimeZoneInfo.ConvertTime(new DateTimeOffset(new DateTime(y, mo, d, h, 0, 0), istanbul.GetUtcOffset(new DateTime(y, mo, d, h, 0, 0))), TimeZoneInfo.Local).DateTime;
    Check(cal.IsInvitation && ev.Title == "NW-1042 ilk numune toplantısı" && ev.Location == "Toplantı Odası 2" && ev.Notes == "İlk numune, FAI ve 3.1 sertifika\ngörüşmesi", "Outlook text fields: " + ev.Title + "|" + ev.Notes);
    Check(ev.Start == Local(2026, 10, 26, 10) && ev.End == Local(2026, 10, 26, 11), "Outlook custom TZID resolved: " + ev.Start);
    Check(ev.Organizer == "Kalite, Ayşe <ayse@ornek.test>" && ev.Attendees.Single() == "Tom Becker <tom@contoso.example>" && ev.Sequence == 2 && ev.ReminderMinutes == 30 && ev.Busy == MailStudio.Services.Calendar.BusyStatus.Tentative, "organizer/attendee/alarm");
    var occ = MailStudio.Services.Calendar.Recurrence.Expand(ev, new DateTime(2026, 10, 1), new DateTime(2027, 1, 1)).ToList();
    Check(occ.Count == 10 && occ.All(o => o.Start.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday) && !occ.Any(o => o.Start.Date == Local(2026, 10, 28, 10).Date), "weekly MO,WE until 30 Nov without the deleted Wednesday: " + occ.Count);

    var google = "BEGIN:VCALENDAR\nMETHOD:REQUEST\nBEGIN:VEVENT\nDTSTART:20261103T080000Z\nDTEND:20261103T083000Z\nRRULE:FREQ=DAILY;COUNT=3\nUID:g1@google.com\nSUMMARY:Stand-up\nATTENDEE;CN=demo@mailstudio.example;PARTSTAT=NEEDS-ACTION:mailto:demo@mailstudio.example\nEND:VEVENT\nBEGIN:VEVENT\nDTSTART;VALUE=DATE:20261029\nDTEND;VALUE=DATE:20261030\nSUMMARY:Cumhuriyet Bayramı\nUID:allday-1\nEND:VEVENT\nEND:VCALENDAR";
    var g = MailStudio.Services.Calendar.Ics.Parse(google);
    var standup = g.Events.First(); var holiday = g.Events.Last();
    Check(standup.Start == new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc).ToLocalTime() && MailStudio.Services.Calendar.Recurrence.Expand(standup, DateTime.MinValue.AddYears(1), DateTime.MaxValue.AddYears(-1)).Count() == 3, "Google UTC + COUNT");
    Check(holiday.AllDay && holiday.Start == new DateTime(2026, 10, 29) && holiday.End == new DateTime(2026, 10, 30) && holiday.Attendees.Count == 0, "all-day date event");

    var rule2 = MailStudio.Services.Calendar.Ics.ParseRule("FREQ=MONTHLY;BYDAY=2TU")!;
    var secondTuesday = new MailStudio.Services.Calendar.CalendarEvent { Start = new DateTime(2026, 10, 13, 9, 0, 0), End = new DateTime(2026, 10, 13, 10, 0, 0), Repeat = rule2 };
    var st = MailStudio.Services.Calendar.Recurrence.Expand(secondTuesday, new DateTime(2026, 10, 1), new DateTime(2027, 1, 1)).Select(o => o.Start.Day).ToList();
    Check(st.SequenceEqual(new[] { 13, 10, 8 }), "2nd Tuesday monthly: " + string.Join(",", st));
    var lastFriday = new MailStudio.Services.Calendar.CalendarEvent { Start = new DateTime(2026, 10, 30, 16, 0, 0), End = new DateTime(2026, 10, 30, 17, 0, 0), Repeat = MailStudio.Services.Calendar.Ics.ParseRule("FREQ=MONTHLY;BYDAY=FR;BYSETPOS=-1") };
    Check(MailStudio.Services.Calendar.Recurrence.Expand(lastFriday, new DateTime(2026, 10, 1), new DateTime(2027, 1, 1)).Select(o => o.Start.Day).SequenceEqual(new[] { 30, 27, 25 }), "last Friday (BYSETPOS=-1)");
    var day31 = new MailStudio.Services.Calendar.CalendarEvent { Start = new DateTime(2026, 10, 31, 9, 0, 0), End = new DateTime(2026, 10, 31, 9, 30, 0), Repeat = new() { Frequency = MailStudio.Services.Calendar.RepeatFrequency.Monthly } };
    Check(MailStudio.Services.Calendar.Recurrence.Expand(day31, new DateTime(2026, 10, 1), new DateTime(2027, 3, 1)).Select(o => o.Start.Month).SequenceEqual(new[] { 10, 12, 1 }), "31st skips short months");
    var biweekly = new MailStudio.Services.Calendar.CalendarEvent { Start = new DateTime(2026, 10, 19, 14, 0, 0), End = new DateTime(2026, 10, 19, 15, 0, 0), Repeat = MailStudio.Services.Calendar.Ics.ParseRule("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO") };
    Check(MailStudio.Services.Calendar.Recurrence.Expand(biweekly, new DateTime(2026, 10, 1), new DateTime(2026, 12, 1)).Select(o => o.Start.Day).SequenceEqual(new[] { 19, 2, 16, 30 }), "every 2 weeks");
    var workdays = MailStudio.Services.Calendar.Ics.ParseRule("FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR")!;
    Check(workdays.IsWorkdays, "DAILY;BYDAY=MO..FR is 'every weekday'");

    var store = new MailStudio.Services.Calendar.CalendarStore(Path.Combine(root, "calendar.json"));
    var applied = store.ApplyInvitation(cal, MailStudio.Services.Calendar.MeetingResponse.Accepted, "acc|INBOX|7");
    Check(applied != null && store.Events.Count == 1 && applied.Response == MailStudio.Services.Calendar.MeetingResponse.Accepted, "invitation accepted into calendar");
    var older = MailStudio.Services.Calendar.Ics.Parse(outlook.Replace("SEQUENCE:2", "SEQUENCE:1").Replace("Toplantı Odası 2", "Eski oda"));
    store.ApplyInvitation(older, MailStudio.Services.Calendar.MeetingResponse.Accepted, null);
    Check(store.Events.Single().Location == "Toplantı Odası 2", "older SEQUENCE does not overwrite");
    store.ApplyInvitation(MailStudio.Services.Calendar.Ics.Parse(outlook.Replace("METHOD:REQUEST", "METHOD:CANCEL").Replace("SEQUENCE:2", "SEQUENCE:3")), MailStudio.Services.Calendar.MeetingResponse.None, null);
    Check(store.Events.Single().Cancelled, "cancellation marks the meeting cancelled");
    Check(store.Import(g.Events) == (2, 0) && store.Import(MailStudio.Services.Calendar.Ics.Parse(google).Events) == (0, 2) && store.Events.Count == 3, "re-import updates by UID");

    var now = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc).ToLocalTime().AddMinutes(-10);
    var meeting = new MailStudio.Services.Calendar.CalendarEvent { Title = "Hatırlat", Start = now.AddMinutes(10), End = now.AddMinutes(40), ReminderMinutes = 15 };
    store.Save(meeting);
    var due = store.DueReminders(now);
    Check(due.Any(o => o.Event.Id == meeting.Id), "reminder due 15 min before");
    store.Snooze(due.Where(o => o.Event.Id == meeting.Id), now.AddMinutes(5));
    Check(!store.DueReminders(now).Any(o => o.Event.Id == meeting.Id) && store.DueReminders(now.AddMinutes(6)).Any(o => o.Event.Id == meeting.Id), "snooze");
    store.Dismiss(store.DueReminders(now.AddMinutes(6)).Where(o => o.Event.Id == meeting.Id));
    Check(!store.DueReminders(now.AddMinutes(7)).Any(o => o.Event.Id == meeting.Id), "dismiss");
    var series = store.Events.First(e => e.Title == "Stand-up");
    store.DeleteOccurrence(series, MailStudio.Services.Calendar.Recurrence.Expand(series, DateTime.MinValue.AddYears(1), DateTime.MaxValue.AddYears(-1)).ElementAt(1).Start);
    Check(MailStudio.Services.Calendar.Recurrence.Expand(store.Find(series.Id)!, DateTime.MinValue.AddYears(1), DateTime.MaxValue.AddYears(-1)).Count() == 2, "delete one occurrence");
    Check(store.Conflicts(meeting.Start.AddMinutes(10), meeting.End.AddMinutes(10)).Any(o => o.Event.Id == meeting.Id), "conflict detection");
    var reloaded = new MailStudio.Services.Calendar.CalendarStore(Path.Combine(root, "calendar.json"));
    Check(reloaded.Events.Count == 4 && reloaded.Find(series.Id)!.Exceptions.Count == 1, "saved to disk");

    var written = MailStudio.Services.Calendar.Ics.WriteCalendar(new[] { applied!, holiday, store.Find(series.Id)! });
    Check(written.Split("\r\n").All(l => System.Text.Encoding.UTF8.GetByteCount(l) <= 75), "lines folded to 75 octets");
    var back = MailStudio.Services.Calendar.Ics.Parse(written);
    var b1 = back.Events.First(e => e.Uid == applied!.Uid);
    Check(b1.Start == applied!.Start && b1.Title == applied.Title && b1.Notes == applied.Notes && b1.Repeat!.Days.Count == 2 && b1.Repeat.Until!.Value.Date == applied.Repeat!.Until!.Value.Date && b1.Exceptions.Count == 1, "round trip keeps times, text, RRULE and EXDATE");
    Check(back.Events.Any(e => e.AllDay && e.Start == holiday.Start) && MailStudio.Services.Calendar.Recurrence.Expand(back.Events.First(e => e.Uid == series.Uid), DateTime.MinValue.AddYears(1), DateTime.MaxValue.AddYears(-1)).Count() == 2, "round trip all-day and exceptions");
    var reply = MailStudio.Services.Calendar.Ics.WriteCalendar(new[] { applied }, "REPLY", replyFrom: "Demo <demo@mailstudio.example>", partStat: "TENTATIVE");
    var rp = MailStudio.Services.Calendar.Ics.Parse(reply);
    Check(rp.IsReply && rp.Replies["demo@mailstudio.example"] == "TENTATIVE" && !reply.Contains("DESCRIPTION"), "meeting reply");

    var tasks = new MailStudio.Services.Calendar.TaskStore(Path.Combine(root, "tasks.json"));
    var t1 = new MailStudio.Services.Calendar.TaskItem { Title = "Teklifi gönder", DueDate = DateTime.Today.AddDays(-1), ReminderAt = DateTime.Now.AddMinutes(-1), SourceMail = "acc|INBOX|1" };
    tasks.Save(t1);
    Check(t1.IsOverdue && tasks.DueReminders(DateTime.Now).Count == 1, "overdue + reminder");
    var lines = MailStudio.Services.Calendar.TaskStore.ParseTaskLines("Görevler:\n- [ ] Teklifi gönder\n1. Resmi kontrol et (rev B)\n* **Malzeme sertifikası iste**\nNormal satır");
    Check(lines.SequenceEqual(new[] { "Teklifi gönder", "Resmi kontrol et (rev B)", "Malzeme sertifikası iste" }), "AI task lines: " + string.Join("|", lines));
    var added = tasks.AddRange(lines.Select(l => new MailStudio.Services.Calendar.TaskItem { Title = l, SourceMail = "acc|INBOX|1" }));
    Check(added == 2 && tasks.Tasks.Count == 3, "duplicate open task skipped");
    tasks.SetCompleted(t1, true);
    Check(new MailStudio.Services.Calendar.TaskStore(Path.Combine(root, "tasks.json")).Find(t1.Id)!.CompletedAt != null && tasks.DueReminders(DateTime.Now).Count == 0, "completed task saved, no reminder");
    var shared = Path.Combine(root, "tasks-shared.json");
    var app = new MailStudio.Services.Calendar.TaskStore(shared); var agent = new MailStudio.Services.Calendar.TaskStore(shared);
    app.Save(new MailStudio.Services.Calendar.TaskItem { Title = "from the app" });
    agent.Save(new MailStudio.Services.Calendar.TaskItem { Title = "from the agent" });
    app.Save(new MailStudio.Services.Calendar.TaskItem { Title = "app again" });
    Check(new MailStudio.Services.Calendar.TaskStore(shared).Tasks.Count == 3 && agent.ReloadIfChanged() && agent.Tasks.Count == 3, "app and MCP agent edits to the same task file are both kept");
}));
await Test("rules: condition and action matrix, order/stop, FakeImap move/flag, store roundtrip and AI parsing", async () =>
{
    var storePath = Path.Combine(root, "rules.json");
    var store = new RuleStore(storePath);
    Check(store.Rules.Count == 0, "initial store empty");

    // Condition tests
    var h1 = new MailHeader
    {
        AccountId = "synthetic", Folder = "INBOX", Uid = 2, UidValidity = 77,
        FromName = "Anna Keller", FromAddress = "anna.keller@example.test",
        To = "machinist@example.test", Subject = "Quotation request for flanges",
        Preview = "Please provide quotation for 120 pcs",
        HasAttachments = true, Size = 25000, Importance = "high"
    };
    var atts = new List<AttachmentInfo> { new() { FileName = "drawing.pdf" }, new() { FileName = "model.stp" } };

    var cFrom = new RuleCondition { Field = "from", Value = "anna;keller" };
    var cTo = new RuleCondition { Field = "to", Value = "machinist" };
    var cSub = new RuleCondition { Field = "subject", Value = "quotation;flange" };
    var cBody = new RuleCondition { Field = "body", Value = "120 pcs" };
    var cAtt = new RuleCondition { Field = "has_attachment" };
    var cType = new RuleCondition { Field = "attachment_type", Value = "pdf;stp" };
    var cAcc = new RuleCondition { Field = "account", Value = "synthetic" };
    var cFld = new RuleCondition { Field = "folder", Value = "INBOX" };
    var cImp = new RuleCondition { Field = "importance", Value = "high" };
    var cSize1 = new RuleCondition { Field = "size_larger_kb", Value = "20" };
    var cSize2 = new RuleCondition { Field = "size_smaller_kb", Value = "30" };

    Check(RuleEngine.MatchesCondition(cFrom, h1), "from matches");
    Check(RuleEngine.MatchesCondition(cTo, h1), "to matches");
    Check(RuleEngine.MatchesCondition(cSub, h1), "subject matches");
    Check(RuleEngine.MatchesCondition(cBody, h1, "Please provide quotation for 120 pcs"), "body matches");
    Check(RuleEngine.MatchesCondition(cAtt, h1), "has_attachment matches");
    Check(RuleEngine.MatchesCondition(cType, h1, attachments: atts), "attachment_type matches");
    Check(RuleEngine.MatchesCondition(cAcc, h1), "account matches");
    Check(RuleEngine.MatchesCondition(cFld, h1), "folder matches");
    Check(RuleEngine.MatchesCondition(cImp, h1), "importance matches");
    Check(RuleEngine.MatchesCondition(cSize1, h1), "size_larger_kb matches");
    Check(RuleEngine.MatchesCondition(cSize2, h1), "size_smaller_kb matches");

    // MatchAll: AND vs OR
    var ruleAnd = new MailRule { MatchAll = true, Conditions = { cFrom, new RuleCondition { Field = "subject", Value = "nonexistent" } } };
    Check(!RuleEngine.Matches(ruleAnd, h1), "AND fails when one condition fails");
    var ruleOr = new MailRule { MatchAll = false, Conditions = { cFrom, new RuleCondition { Field = "subject", Value = "nonexistent" } } };
    Check(RuleEngine.Matches(ruleOr, h1), "OR succeeds when one condition matches");

    // Rule ordering and stop processing
    var executedSkills = new List<string>();
    var taskStore = new MailStudio.Services.Calendar.TaskStore(Path.Combine(root, "rule-tasks.json"));
    var cache = Cache("rules-cache");
    cache.UpsertHeaders(new[] { h1 });
    cache.SaveAttachments("synthetic", "INBOX", 2, atts);

    await using var server = new FakeImap();
    using var mail = Service(cache, "rules-mail");
    var account = Account(server);

    var r1 = new MailRule
    {
        Name = "Flag and category",
        StopProcessing = false,
        Conditions = { cFrom },
        Actions = {
            new RuleAction { Type = "mark_as_flagged" },
            new RuleAction { Type = "set_category", Value = "Important" },
            new RuleAction { Type = "create_task", Value = "Review" },
            new RuleAction { Type = "run_skill", Value = "summarize" }
        }
    };
    var r2 = new MailRule
    {
        Name = "Move and stop",
        StopProcessing = true,
        Conditions = { cSub },
        Actions = {
            new RuleAction { Type = "move_to_folder", Value = "Trash" }
        }
    };
    var r3 = new MailRule
    {
        Name = "Never reached",
        StopProcessing = true,
        Conditions = { cFrom },
        Actions = {
            new RuleAction { Type = "mark_as_read" }
        }
    };

    var results = await RuleEngine.ApplyRulesAsync(
        new[] { r1, r2, r3 },
        h1,
        account,
        mail,
        cache,
        taskStore,
        (s, _) => { executedSkills.Add(s); return Task.CompletedTask; }
    );

    Check(results.Count == 2, "exactly two rules ran before stop_processing");
    Check(h1.IsFlagged, "action mark_as_flagged executed");
    Check(h1.Category == "Important", "action set_category executed");
    Check(taskStore.Tasks.Any(t => t.Title == "Review: " + h1.Subject), "action create_task executed");
    Check(executedSkills.Contains("summarize"), "action run_skill executed");
    Check(results[1].Moved && results[1].AppliedActions.Any(a => a.Contains("Trash")), "action move_to_folder executed");
    Check(!h1.IsRead, "third rule was stopped by stop_processing");

    // Empty values never match everything; such rules cannot be saved
    Check(!RuleEngine.MatchesCondition(new RuleCondition { Field = "from", Value = "" }, h1), "empty 'from' value matches nothing");
    Check(!RuleEngine.MatchesCondition(new RuleCondition { Field = "subject", Value = " ; " }, h1), "separator-only value matches nothing");
    Check(RuleEngine.Validate(new MailRule { Name = "x", Conditions = { new RuleCondition { Field = "from", Value = "" } }, Actions = { new RuleAction { Type = "move_to_trash" } } }) != null, "rule with an empty condition is refused");
    Check(RuleEngine.Validate(new MailRule { Name = "x", Conditions = { new RuleCondition { Field = "from", Value = "a@b" } }, Actions = { new RuleAction { Type = "move_to_folder", Value = "" } } }) != null, "move without folder is refused");
    Check(RuleEngine.Validate(new MailRule { Name = "x", Conditions = { new RuleCondition { Field = "has_attachment" } }, Actions = { new RuleAction { Type = "mark_as_read" } } }) == null, "has_attachment needs no value");
    var norm = new MailRule { Name = "n", Conditions = { new RuleCondition { Field = "FROM", Value = "a" } }, Actions = { new RuleAction { Type = "mark_read" }, new RuleAction { Type = "flag" } } };
    RuleEngine.Normalize(norm);
    Check(norm.Conditions[0].Field == "from" && norm.Actions[0].Type == "mark_as_read" && norm.Actions[1].Type == "mark_as_flagged", "agent/AI action names normalized");

    // A move runs last: actions listed after it still apply to the right message
    server.Messages[3] = "Message-Id: <three@test>\r\nSubject: order\r\n\r\nx";
    var h2 = new MailHeader { AccountId = "synthetic", Folder = "INBOX", Uid = 3, UidValidity = h1.UidValidity, MessageId = "three@test", FromAddress = "anna@example.test", Subject = "order" };
    cache.UpsertHeaders(new[] { h2 });
    var moveFirst = new MailRule { Name = "move first", Conditions = { new RuleCondition { Field = "subject", Value = "order" } },
        Actions = { new RuleAction { Type = "move_to_folder", Value = "Trash" }, new RuleAction { Type = "mark_as_read" } } };
    var res2 = await RuleEngine.ApplyRulesAsync(new[] { moveFirst }, h2, account, mail, cache, taskStore);
    Check(h2.IsRead && res2[0].Moved && res2[0].AppliedActions[0].Contains("Okundu", StringComparison.OrdinalIgnoreCase) | res2[0].AppliedActions[0].Contains("read", StringComparison.OrdinalIgnoreCase), "mark-as-read applied before the move");

    // Store persistence
    store.Rules.AddRange(new[] { r1, r2 });
    store.Save();
    var reloaded = new RuleStore(storePath);
    Check(reloaded.Rules.Count == 2 && reloaded.Rules[0].Name == "Flag and category" && reloaded.Rules[1].StopProcessing, "rules saved and reloaded from json");

    // AI rule parsing
    var aiJson = """
    {
        "Name": "Anna'dan gelen teklifler",
        "StopProcessing": true,
        "MatchAll": true,
        "Conditions": [
            { "Field": "from", "Value": "anna.keller@" },
            { "Field": "subject", "Value": "teklif;flanş" }
        ],
        "Actions": [
            { "Type": "move_to_folder", "Value": "Teklifler" },
            { "Type": "mark_as_flagged" }
        ]
    }
    """;
    var parsed = RuleEngine.ParseAi(aiJson);
    Check(parsed.Name == "Anna'dan gelen teklifler" && parsed.Conditions.Count == 2 && parsed.Actions.Count == 2 && parsed.StopProcessing, "AI rule parsed successfully");
    Check(RuleEngine.AiPrompt("test prompt").Contains("JSON"), "AI prompt generated");
});
await Test("follow-up: reply clears follow-up; expired deadline creates task", () => Sync(() =>
{
    var storePath = Path.Combine(root, "test-followups.json");
    var fuStore = new FollowUpStore(storePath);
    var taskStore = new MailStudio.Services.Calendar.TaskStore(Path.Combine(root, "test-tasks.json"));

    var item1 = new FollowUpItem
    {
        AccountId = "acc1",
        MessageId = "msg-101@test.example",
        Subject = "Teklif talebi flanş",
        Recipients = "customer@example.com",
        SentDate = DateTimeOffset.Now.AddDays(-5),
        DueDays = 3,
        DueDate = DateTimeOffset.Now.AddDays(-2),
        IsResolved = false
    };

    var item2 = new FollowUpItem
    {
        AccountId = "acc1",
        MessageId = "msg-102@test.example",
        Subject = "Braket revizyonu",
        Recipients = "supplier@example.com",
        SentDate = DateTimeOffset.Now.AddDays(-1),
        DueDays = 3,
        DueDate = DateTimeOffset.Now.AddDays(2),
        IsResolved = false
    };

    fuStore.Add(item1);
    fuStore.Add(item2);
    Check(fuStore.FollowUps.Count == 2, "2 follow-up items added");

    // 1. CheckExpired creates a task for item1, but not for item2
    var createdCount = fuStore.CheckExpired(taskStore, DateTimeOffset.Now);
    Check(createdCount == 1, "1 expired follow-up task created");
    Check(item1.TaskCreated && !string.IsNullOrEmpty(item1.TaskId), "item1 marked TaskCreated with TaskId");
    Check(!item2.TaskCreated, "item2 not expired, no task created");

    var task = taskStore.Find(item1.TaskId!);
    Check(task != null, "task found in TaskStore");
    Check(task!.Title.Contains("Teklif talebi flanş"), "task title contains subject: " + task.Title);
    Check(task.Category == L.T("Cevap bekleniyor"), "task category is Cevap bekleniyor: " + task.Category);
    Check(task.IsOverdue, "task is overdue");
    Check(!task.Completed, "task is not completed yet");

    // 2. Incoming reply for item1 clears the follow-up and auto-completes the linked task
    var reply1 = new MailHeader
    {
        AccountId = "acc1",
        Folder = "INBOX",
        Uid = 991,
        MessageId = "reply-991@example.com",
        InReplyTo = "<msg-101@test.example>",
        Subject = "Ynt: Teklif talebi flanş",
        FromAddress = "customer@example.com"
    };

    var resolvedCount = fuStore.CheckReplies(new[] { reply1 }, taskStore);
    Check(resolvedCount == 1, "reply resolved item1");
    Check(item1.IsResolved, "item1 IsResolved == true");
    Check(task.Completed, "linked task automatically marked completed");

    // 3. Incoming reply for item2 clears follow-up via References match
    var reply2 = new MailHeader
    {
        AccountId = "acc1",
        Folder = "INBOX",
        Uid = 992,
        MessageId = "reply-992@example.com",
        References = new List<string> { "<other@example.com>", "<msg-102@test.example>" },
        Subject = "Re: Braket revizyonu",
        FromAddress = "supplier@example.com"
    };

    var resolvedCount2 = fuStore.CheckReplies(new[] { reply2 }, taskStore);
    Check(resolvedCount2 == 1, "reply with References resolved item2");
    Check(item2.IsResolved, "item2 IsResolved == true");

    // 4. Persistence round-trip
    var reloadedStore = new FollowUpStore(storePath);
    Check(reloadedStore.FollowUps.Count == 2 && reloadedStore.FollowUps.All(f => f.IsResolved), "follow-ups saved and reloaded with resolved status");
}));
await Test("undo send and scheduled send: undo aborts before SMTP; scheduled send waits until due time", async () =>
{
    Func<AccountConfig, OutboxItem, Task> SendWith(MailService m) => (a, i) => m.SendAsync(a, i.Request);
    await using var imap = new FakeImap();
    await using var smtp = new FakeSmtp();
    var acc = Account(imap, smtp);
    var cache = Cache("outbox-test");
    var service = Service(cache, "outbox-test");

    var outboxPath = Path.Combine(root, "test-outbox.json");
    var outbox = new OutboxStore(outboxPath);

    // 1. Undo Send: queued with 10s delay. Before delay expires, NO SMTP packets are sent.
    var reqUndo = new ComposeRequest { To = "alice@example.test", Subject = "Urgent update", Body = "Sent with undo" };
    var undoItem = new OutboxItem
    {
        AccountId = acc.Id,
        Request = reqUndo,
        ScheduledTime = DateTimeOffset.Now.AddSeconds(10),
        IsUndoSend = true,
        Status = "Scheduled"
    };
    outbox.Add(undoItem);

    // Attempt processing at current time: should not send because scheduled time is in future
    var sentUndo0 = await outbox.ProcessDueSendsAsync(_ => acc, SendWith(service), DateTimeOffset.Now);
    Check(sentUndo0 == 0, "undo item not sent immediately");
    Check(smtp.Accepted == 0, "no SMTP packets sent while in undo window");

    // User clicks "Geri Al" (Undo): item is removed from outbox
    outbox.Remove(undoItem.Id);

    // Time advances past 10 seconds: item was removed, so nothing sends
    var sentUndoPast = await outbox.ProcessDueSendsAsync(_ => acc, SendWith(service), DateTimeOffset.Now.AddSeconds(15));
    Check(sentUndoPast == 0, "cancelled undo item is never sent");
    Check(smtp.Accepted == 0, "still zero SMTP packets sent after undo window");

    // 2. Scheduled Send (Send Later): scheduled for 2 hours in the future
    var reqScheduled = new ComposeRequest { To = "bob@example.test", Subject = "Morning Report", Body = "Scheduled for 9am" };
    var scheduledTime = DateTimeOffset.Now.AddHours(2);
    var schedItem = new OutboxItem
    {
        AccountId = acc.Id,
        Request = reqScheduled,
        ScheduledTime = scheduledTime,
        IsUndoSend = false,
        Status = "Scheduled"
    };
    outbox.Add(schedItem);

    // Test before scheduled time: should NOT send
    var sentBefore = await outbox.ProcessDueSendsAsync(_ => acc, SendWith(service), DateTimeOffset.Now.AddHours(1));
    Check(sentBefore == 0, "scheduled item not sent before its time");
    Check(smtp.Accepted == 0, "no SMTP packets before scheduled time");

    // Test persistence: outbox saved and reloaded
    var reloadedOutbox = new OutboxStore(outboxPath);
    Check(reloadedOutbox.Items.Count == 1 && reloadedOutbox.Items[0].Request.Subject == "Morning Report", "scheduled item survived restart in outbox.json");

    // Test when due time arrives: sends via SMTP and clears from outbox
    var sentOnTime = await reloadedOutbox.ProcessDueSendsAsync(_ => acc, SendWith(service), scheduledTime.AddMinutes(1));
    Check(sentOnTime == 1, "scheduled item sent when due");
    Check(smtp.Accepted == 1, "exactly one SMTP message delivered");
    Check(smtp.Mime.Contains("Morning Report"), "delivered message contains correct subject");
    Check(reloadedOutbox.Items.Count == 0, "sent item cleared from outbox queue");

    // 3. Two queue runs at the same time never send one item twice
    var twice = new OutboxItem { AccountId = acc.Id, Request = new ComposeRequest { To = "carol@example.test", Subject = "Once only", Body = "x" }, ScheduledTime = DateTimeOffset.Now.AddSeconds(-1) };
    reloadedOutbox.Add(twice);
    var slowSends = 0;
    Func<AccountConfig, OutboxItem, Task> slow = async (a, i) => { Interlocked.Increment(ref slowSends); await Task.Delay(300); await service.SendAsync(a, i.Request); };
    var runs = await Task.WhenAll(reloadedOutbox.ProcessDueSendsAsync(_ => acc, slow), reloadedOutbox.ProcessDueSendsAsync(_ => acc, slow));
    Check(runs.Sum() == 1 && slowSends == 1 && smtp.Accepted == 2, $"concurrent queue runs sent the item exactly once (sends {slowSends}, smtp {smtp.Accepted})");

    // 4. Restart: a due item is HELD (asked), never sent automatically; an item that was being sent becomes Uncertain
    var held = new OutboxItem { AccountId = acc.Id, Request = new ComposeRequest { To = "dave@example.test", Subject = "Overdue", Body = "x" }, ScheduledTime = DateTimeOffset.Now.AddHours(1) };
    var undoLeft = new OutboxItem { AccountId = acc.Id, Request = new ComposeRequest { To = "erin@example.test", Subject = "Undo window at exit", Body = "x" }, ScheduledTime = DateTimeOffset.Now.AddHours(1), IsUndoSend = true };
    var inFlight = new OutboxItem { AccountId = acc.Id, Request = new ComposeRequest { To = "frank@example.test", Subject = "Was sending", Body = "x" }, ScheduledTime = DateTimeOffset.Now.AddMinutes(-5), Status = OutboxStatus.Sending };
    reloadedOutbox.Add(held); reloadedOutbox.Add(undoLeft); reloadedOutbox.Add(inFlight);
    var afterRestart = new OutboxStore(outboxPath);
    afterRestart.Load(DateTimeOffset.Now.AddHours(2));
    Check(afterRestart.Find(held.Id)?.Status == OutboxStatus.Held, "overdue scheduled mail is held for the user");
    Check(afterRestart.Find(undoLeft.Id)?.Status == OutboxStatus.Held, "mail left in the undo window at exit is held, not lost");
    Check(afterRestart.Find(inFlight.Id)?.Status == OutboxStatus.Uncertain, "mail interrupted while sending is Uncertain");
    var autoSent = await afterRestart.ProcessDueSendsAsync(_ => acc, SendWith(service), DateTimeOffset.Now.AddHours(3));
    Check(autoSent == 0 && smtp.Accepted == 2, "nothing held or uncertain is sent automatically");
    Check(afterRestart.Release(held.Id), "user releases a held mail");
    var released = await afterRestart.ProcessDueSendsAsync(_ => acc, SendWith(service));
    Check(released == 1 && smtp.Accepted == 3, "released mail sent once");
    Check(!afterRestart.Remove("nope") && afterRestart.Items.Count == 2, "unknown id is not removed");

    // 5. A corrupt outbox.json is kept as a backup, not silently lost
    File.WriteAllText(outboxPath, "{ bozuk");
    var corrupt = new OutboxStore(outboxPath);
    Check(corrupt.Items.Count == 0 && Directory.GetFiles(root, "test-outbox.json.bozuk-*").Length == 1, "corrupt outbox backed up");
});

await Test("missing attachment warning: Turkish/English keywords, false alarms, and quote stripping", () => Sync(() =>
{
    // 1. Positive Turkish triggers (0 attachments -> ShouldWarn is true)
    string? kw;
    Check(AttachmentReminder.ShouldWarn(null, "Detaylı teklif ekte bilginize sunulmuştur.", 0, null, null, null, out kw) && kw == "ekte", "TR keyword: ekte");
    Check(AttachmentReminder.ShouldWarn(null, "Yeni çizimleri ekledim, kontrol eder misiniz?", 0, null, null, null, out kw) && kw == "ekledim", "TR keyword: ekledim");
    Check(AttachmentReminder.ShouldWarn(null, "İlişikteki teknik şartnameye göre üretilecek.", 0, null, null, null, out kw) && kw == "ilişikteki", "TR keyword: ilişikteki");
    Check(AttachmentReminder.ShouldWarn(null, "Teknik çizim ektedir.", 0, null, null, null, out kw) && kw == "ektedir", "TR keyword: ektedir");
    Check(AttachmentReminder.ShouldWarn(null, "Ekli dosyada revizyon listesi mevcut.", 0, null, null, null, out kw) && kw == "ekli", "TR keyword: ekli");
    Check(AttachmentReminder.ShouldWarn(null, "Ek dosyada tüm detaylar verilmiştir.", 0, null, null, null, out kw) && kw!.StartsWith("ek dosya"), "TR keyword: ek dosya");
    Check(AttachmentReminder.ShouldWarn(null, "Parça modeli ilişiktedir.", 0, null, null, null, out kw) && kw == "ilişiktedir", "TR keyword: ilişiktedir");
    Check(AttachmentReminder.ShouldWarn(null, "DXF dosyasını ekliyorum.", 0, null, null, null, out kw) && kw == "ekliyorum", "TR keyword: ekliyorum");

    // 2. Positive English triggers (0 attachments -> ShouldWarn is true)
    Check(AttachmentReminder.ShouldWarn(null, "Please find attached the revised drawing.", 0, null, null, null, out kw) && kw == "attached", "EN keyword: attached");
    Check(AttachmentReminder.ShouldWarn(null, "See attachment for full tolerances.", 0, null, null, null, out kw) && kw == "attachment", "EN keyword: attachment");
    Check(AttachmentReminder.ShouldWarn(null, "The purchase order is enclosed.", 0, null, null, null, out kw) && kw == "enclosed", "EN keyword: enclosed");
    Check(AttachmentReminder.ShouldWarn(null, "I am enclosing the inspection report.", 0, null, null, null, out kw) && kw == "enclosing", "EN keyword: enclosing");
    Check(AttachmentReminder.ShouldWarn(null, "See attached file.", 0, null, null, null, out kw) && (kw == "attached" || kw!.StartsWith("see attach")), "EN keyword: see attached");

    // 3. Negative false alarm cases (0 attachments -> ShouldWarn is false)
    Check(!AttachmentReminder.ShouldWarn(null, "Ek olarak yarınki toplantı saat 10:00'da.", 0, null, null, null, out _), "false alarm: ek olarak");
    Check(!AttachmentReminder.ShouldWarn(null, "Konuyla ilgili ek bilgi gerekirse lütfen arayınız.", 0, null, null, null, out _), "false alarm: ek bilgi");
    Check(!AttachmentReminder.ShouldWarn(null, "Ek bir sorunuz olursa yanıtlamaktan memnuniyet duyarım.", 0, null, null, null, out _), "false alarm: ek bir");
    Check(!AttachmentReminder.ShouldWarn(null, "Proje için 2 günlük ek süre rica ediyoruz.", 0, null, null, null, out _), "false alarm: ek süre");
    Check(!AttachmentReminder.ShouldWarn(null, "Bu revizyon için ek ücret alınmayacaktır.", 0, null, null, null, out _), "false alarm: ek ücret");
    Check(!AttachmentReminder.ShouldWarn(null, "Ek protokol imzalandı.", 0, null, null, null, out _), "false alarm: ek protokol");
    Check(!AttachmentReminder.ShouldWarn(null, "No strings attached for this promotional offer.", 0, null, null, null, out _), "false alarm: no strings attached");
    Check(!AttachmentReminder.ShouldWarn(null, "The customer has an emotional attachment to the legacy model.", 0, null, null, null, out _), "false alarm: emotional attachment");
    Check(!AttachmentReminder.ShouldWarn(null, "Download link: https://example.test/attached_documents/part.zip", 0, null, null, null, out _), "false alarm: URL containing attached");
    Check(!AttachmentReminder.ShouldWarn(null, "Please write to attachment-service@company.test for questions.", 0, null, null, null, out _), "false alarm: email address containing attachment");

    // 4. Quoted text stripping (keyword in original message, NOT in user's reply)
    var replyBody = "Tamamdır, anlaştık.\n\n-----Original Message-----\nFrom: customer@example.test\nSubject: Order\nPlease find attached the quote.";
    Check(!AttachmentReminder.ShouldWarn(null, replyBody, 0, null, null, null, out _), "quoted text in reply does not trigger warning");

    var trReplyBody = "Revizyonu aldım, teşekkürler.\n\n> Ekteki dosyayı inceleyip onaylayabilir misiniz?\n> Saygılarımla";
    Check(!AttachmentReminder.ShouldWarn(null, trReplyBody, 0, null, null, null, out _), "quoted text with > does not trigger warning");

    var htmlWithQuote = "<p>Harika haber.</p><div id=\"ms-quote\">----- Özgün İleti -----<br>Ekte teklifimizi iletiyoruz.</div>";
    Check(!AttachmentReminder.ShouldWarn(htmlWithQuote, null, 0, null, null, null, out _), "HTML quote section does not trigger warning");

    // 5. Signature stripping
    var htmlWithSig = "<p>Merhaba,</p><p>Yarın görüşürüz.</p><div id=\"ms-signature\"><p>--<br>Gizlilik Notu: Bu e-posta ve ilişikteki tüm belgeler gizlidir.</p></div>";
    Check(!AttachmentReminder.ShouldWarn(htmlWithSig, null, 0, null, null, null, out _), "HTML signature disclaimer does not trigger warning");

    // 6. When attachment IS present (attachments > 0 -> ShouldWarn is false even if keyword present)
    Check(!AttachmentReminder.ShouldWarn(null, "Teknik resim ektedir.", 1, null, null, null, out _), "no warning when file is actually attached");
    Check(!AttachmentReminder.ShouldWarn(null, "Please find attached the report.", 2, null, null, null, out _), "no warning when 2 files are attached");
}));

await Test("revision comparison: part and rev parsing across patterns (no false revisions in sentences)", () => Sync(() =>
{
    var (p1, r1) = RevisionService.ParsePartAndRevision("NW-1042 rev B");
    Check(p1 == "NW-1042" && r1 == "B", "NW-1042 rev B parsed correctly");
    var (p2, r2) = RevisionService.ParsePartAndRevision("", "bracket-revC.stp");
    Check(p2 == "bracket" && r2 == "C", $"bracket-revC.stp parsed correctly ({p2}/{r2})");
    var (p3, r3) = RevisionService.ParsePartAndRevision("", "delivery-schedule-wk43.xlsx");
    Check(p3 == "delivery-schedule" && r3 == "Wk43", $"delivery-schedule-wk43 parsed correctly ({p3}/{r3})");
    var (p4, r4) = RevisionService.ParsePartAndRevision("", "flange-DN100-revC.pdf");
    Check(p4 == "flange-DN100" && r4 == "C", "flange-DN100-revC parsed correctly");
    var (p5, r5) = RevisionService.ParsePartAndRevision("", "part_R02.dxf");
    Check(p5 == "part" && r5 == "02", "part_R02 parsed correctly");
    var (p6, r6) = RevisionService.ParsePartAndRevision("", "NW-1042_Rev.D.pdf");
    Check(p6 == "NW-1042" && r6 == "D", $"NW-1042_Rev.D parsed ({p6}/{r6})");
    // sentences and plain names must not invent revisions or part numbers
    Check(RevisionService.ParsePartAndRevision("NW-1042 ve rapor").Revision == "", "'ve rapor' is not a revision");
    Check(RevisionService.ParsePartAndRevision("NW-1042 revizyon güncellemesi").Revision == "", "'revizyon güncellemesi' is not a revision");
    Check(RevisionService.ParsePartAndRevision("Teklif talebi").PartNumber == "", "no part number in a plain subject");
    Check(RevisionService.ParsePartAndRevision("", "logo.png") == ("logo", ""), "plain file name: no revision");
    Check(RevisionService.ParsePartAndRevision("", "drawer.pdf").Revision == "", "'drawer' does not contain 'rev'");
    var prev = System.Globalization.CultureInfo.CurrentCulture;
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
    try { Check(RevisionService.ParsePartAndRevision("", "FLANGE_REVISION_C.DXF").Revision == "C", "REVISION with dotless-I culture"); }
    finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
}));

string FindDemoDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var p = Path.Combine(dir.FullName, "src", "MailStudio", "Demo");
        if (Directory.Exists(p)) return p;
        dir = dir.Parent;
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "src", "MailStudio", "Demo");
}

await Test("revision comparison: Excel schedules diff by row key", () => Sync(() =>
{
    var demoDir = FindDemoDir();
    var f1 = Path.Combine(demoDir, "delivery-schedule-wk43.xlsx");
    var f2 = Path.Combine(demoDir, "delivery-schedule-wk44.xlsx");
    Check(File.Exists(f1) && File.Exists(f2), "Excel demo files exist for test");
    var report = RevisionService.CompareExcel(f1, f2);
    Check(report.FileType == "Excel" && report.OldRevision == "Wk43" && report.NewRevision == "Wk44", "Excel report with week revisions");
    var qty = report.Items.FirstOrDefault(i => i.Property.StartsWith("NW-1042") && i.Property.Contains("Qty"));
    Check(qty != null && qty.OldValue == "120" && qty.NewValue == "150", $"NW-1042 quantity 120 → 150 ({qty?.OldValue}→{qty?.NewValue})");
    Check(report.Items.Any(i => i.Property.StartsWith("NW-1042") && i.Property.Contains("Unit price") && i.NewValue == "41"), "NW-1042 unit price 38.5 → 41");
    Check(!report.Items.Any(i => i.Property.StartsWith("NW-1043") && i.Property.Contains("Qty")), "unchanged row has no quantity change");

    // an inserted row does not shift the following rows
    var wbDir = Path.Combine(root, "xlsx-insert"); Directory.CreateDirectory(wbDir);
    string Book(string name, params (string Part, int Qty)[] rows)
    {
        var path = Path.Combine(wbDir, name);
        using var wb = new ClosedXML.Excel.XLWorkbook(); var ws = wb.AddWorksheet("Plan");
        ws.Cell(1, 1).Value = "Part"; ws.Cell(1, 2).Value = "Qty";
        for (int i = 0; i < rows.Length; i++) { ws.Cell(i + 2, 1).Value = rows[i].Part; ws.Cell(i + 2, 2).Value = rows[i].Qty; }
        wb.SaveAs(path); return path;
    }
    var x1 = Book("plan-rev1.xlsx", ("A-100", 5), ("A-200", 7), ("A-300", 9));
    var x2 = Book("plan-rev2.xlsx", ("A-100", 5), ("A-150", 3), ("A-200", 7), ("A-300", 10));
    var ins = RevisionService.CompareExcel(x1, x2);
    Check(ins.Items.Count == 2, $"inserted row + one changed cell only ({ins.Items.Count}: {string.Join(" | ", ins.Items.Select(i => i.Description))})");
    Check(ins.Items.Any(i => i.Kind == RevisionDiffKind.Added && i.Property.Contains("A-150")), "inserted row reported as added");
    Check(ins.Items.Any(i => i.Kind == RevisionDiffKind.Modified && i.Property.StartsWith("A-300") && i.OldValue == "9" && i.NewValue == "10"), "A-300 quantity change found");

    Check(report.ToReplyText().Contains("delivery-schedule"), "ToReplyText contains part");
    Check(report.ToAiPrompt().Contains("imalat") || report.ToAiPrompt().Contains("manufacturing"), "ToAiPrompt contains manufacturing context");
}));

JsonElement J(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

await Test("revision comparison: STEP compares exact worker values and real holes (no estimates)", () => Sync(() =>
{
    var a = J("""
{"overall_size_xyz_mm":[120,80,40],"total_volume_mm3":150000,"total_surface_mm2":42000,"body_count":1,
               "bodies":[{"cylindrical_faces":14,"holes":[{"diameter_mm":6.8,"count":4},{"diameter_mm":20,"count":1}]}],"pmi":[]}
""");
    var b = J("""
{"overall_size_xyz_mm":[120,80,42.5],"total_volume_mm3":148500,"total_surface_mm2":42000,"body_count":1,
               "bodies":[{"cylindrical_faces":14,"holes":[{"diameter_mm":6.8,"count":4},{"diameter_mm":25,"count":1}]}],"pmi":[{"kind":"dimension","value":"Ø25 H7"}]}
""");
    var r = RevisionService.CompareStep("bracket-revB.stp", "bracket-revC.stp", a, b);
    Check(r.OldRevision == "B" && r.NewRevision == "C", "revisions from file names");
    Check(r.Items.Any(i => i.Property == "Yükseklik (Z)" && i.Description.Contains("+2.5")), "height +2.5 mm");
    Check(r.Items.Any(i => i.Category == "Hacim"), "volume change reported");
    Check(!r.Items.Any(i => i.Category == "Yüzey Alanı"), "unchanged area not reported");
    Check(r.Items.Any(i => i.Category == "Delik" && i.Property == "Ø20" && i.Kind == RevisionDiffKind.Removed), "Ø20 hole removed");
    Check(r.Items.Any(i => i.Category == "Delik" && i.Property == "Ø25" && i.Kind == RevisionDiffKind.Added), "Ø25 hole added");
    Check(!r.Items.Any(i => i.Property == "Ø6.8"), "unchanged holes not reported");
    Check(r.Items.Any(i => i.Category == "PMI" && i.Kind == RevisionDiffKind.Added), "new PMI reported");
    // an old worker without hole data: only the honest cylindrical face count
    var c = J("""{"overall_size_xyz_mm":[1,1,1],"total_volume_mm3":1,"total_surface_mm2":1,"body_count":1,"bodies":[{"cylindrical_faces":9}]}""");
    var d = J("""{"overall_size_xyz_mm":[1,1,1],"total_volume_mm3":1,"total_surface_mm2":1,"body_count":1,"bodies":[{"cylindrical_faces":11}]}""");
    var r2 = RevisionService.CompareStep("p-revA.stp", "p-revB.stp", c, d);
    Check(r2.Items.Count == 1 && r2.Items[0].Property == "Silindirik Yüzey Sayısı", "fallback reports cylindrical faces, not 'holes'");
}));

await Test("revision comparison: DXF dimensions and texts as multisets (insertions do not shift)", () => Sync(() =>
{
    var a = J("""
{"layers":["0","OUTLINE","DIMS"],"dimensions":[{"measurement":220},{"measurement":110},{"measurement":24},{"measurement":60}],
               "texts":[{"text":"8x Ø18 THRU EQ. SP. ON PCD 180"},{"text":"Ra 3.2"},{"text":"DWG NO. NW-1042 REV B"},{"text":"MAT. S235JR"},{"text":"SECTION A-A"}]}
""");
    var b = J("""
{"layers":["0","OUTLINE","DIMS","NOTES"],"dimensions":[{"measurement":5},{"measurement":220},{"measurement":110},{"measurement":26},{"measurement":60}],
               "texts":[{"text":"8x Ø20 THRU EQ. SP. ON PCD 180"},{"text":"Ra 1.6"},{"text":"DWG NO. NW-1042 REV C"},{"text":"MAT. S235JR"},{"text":"SECTION A-A"},{"text":"DEBURR ALL EDGES"}]}
""");
    var r = RevisionService.CompareDxf("flange-DN100-revB.dxf", "flange-DN100-revC.dxf", a, b);
    Check(r.Items.Any(i => i.Category == "Katman" && i.NewValue == "NOTES"), "layer added");
    Check(r.Items.Any(i => i.Category == "Ölçü" && i.Kind == RevisionDiffKind.Added && i.NewValue == "5"), "new dimension 5");
    Check(r.Items.Any(i => i.Category == "Ölçü" && i.Kind == RevisionDiffKind.Removed && i.OldValue == "24"), "dimension 24 gone");
    Check(r.Items.Any(i => i.Category == "Ölçü" && i.Kind == RevisionDiffKind.Added && i.NewValue == "26"), "dimension 26 new");
    Check(!r.Items.Any(i => i.Category == "Ölçü" && (i.OldValue == "220" || i.NewValue == "220")), "unchanged 220 not reported despite the inserted dimension");
    Check(r.Items.Any(i => i.Kind == RevisionDiffKind.Modified && i.OldValue == "8x Ø18 THRU EQ. SP. ON PCD 180" && i.NewValue!.Contains("Ø20")), "hole note Ø18 → Ø20");
    Check(r.Items.Any(i => i.Kind == RevisionDiffKind.Modified && i.OldValue == "Ra 3.2" && i.NewValue == "Ra 1.6"), "Ra 3.2 → 1.6");
    Check(r.Items.Any(i => i.Kind == RevisionDiffKind.Modified && i.NewValue == "DWG NO. NW-1042 REV C"), "title REV B → C");
    Check(r.Items.Any(i => i.Kind == RevisionDiffKind.Added && i.NewValue == "DEBURR ALL EDGES"), "new note");
    Check(!r.Items.Any(i => (i.OldValue ?? i.NewValue) == "SECTION A-A" || (i.OldValue ?? i.NewValue) == "MAT. S235JR"), "unchanged texts not reported");
}));

await Test("revision comparison: PDF drawing text (dimensions, title block, notes) and OCR note", () => Sync(() =>
{
    var a = J("""{"content":[{"page":1,"method":"OCR (verify numbers on the drawing)","text":"FLANGE DN100 PN16\nDWG NO. NW-1042 REV B\nMAT. S235JR\n8x Ø18 THRU EQ. SP. ON PCD 180\nØ220\n24 ±0.1\nRa 3.2\nSCALE 1:2"}]}""");
    var b = J("""{"content":[{"page":1,"method":"OCR (verify numbers on the drawing)","text":"FLANGE DN100 PN16\nDWG NO. NW-1042 REV C\nMAT. S235JR\n8x Ø20 THRU EQ. SP. ON PCD 180\nØ220\n26 ±0.1\nRa 1.6\nSCALE 1:2\nBREAK ALL SHARP EDGES"}]}""");
    var r = RevisionService.ComparePdf("flange-DN100-revB.pdf", "flange-DN100-revC.pdf", a, b);
    Check(r.Notes.Any(n => n.Contains("OCR")), "OCR note shown");
    Check(r.Items.Any(i => i.Category == "Antet" && i.OldValue == "NW-1042 REV B" && i.NewValue == "NW-1042 REV C"), $"title block DWG NO changed ({string.Join(" | ", r.Items.Select(i => i.Category + ":" + i.Description))})");
    Check(r.Items.Any(i => i.Category == "Ölçü" && i.OldValue == "Ra3.2" && i.NewValue == "Ra1.6"), "Ra changed");
    Check(r.Items.Any(i => i.Category == "Ölçü" && i.OldValue == "24±0.1" && i.NewValue == "26±0.1" || i.OldValue == "24±0.1" && i.Kind == RevisionDiffKind.Removed), "toleranced 24 changed");
    Check(r.Items.Any(i => i.Category == "Ölçü" && i.OldValue == "8xØ18" && i.NewValue == "8xØ20"), "hole callout 8xØ18 → 8xØ20");
    Check(!r.Items.Any(i => i.OldValue == "Ø220" || i.NewValue == "Ø220"), "unchanged Ø220 not reported");
    Check(r.Items.Any(i => i.Category == "Not" && i.NewValue == "BREAK ALL SHARP EDGES"), "new note reported");
    var empty = RevisionService.ComparePdf("a-revA.pdf", "a-revB.pdf", J("""{"content":[]}"""), b);
    Check(empty.Items.Count == 0 && empty.Notes.Any(n => n.Contains("okunamadı") || n.Contains("could not")), "unreadable PDF says so, reports nothing");
}));

await Test("revision comparison: earlier revisions found in the same correspondent's mails", () => Sync(() =>
{
    var cache = Cache("revision-candidates");
    MailHeader H(uint uid, string from, string subject, int daysAgo) => new()
    { AccountId = "acc", Folder = "INBOX", Uid = uid, FromAddress = from, FromName = from, Subject = subject, Date = DateTimeOffset.Now.AddDays(-daysAgo), HasAttachments = true };
    var revC = H(10, "anna@northwind.example", "NW-1042 rev C", 0);
    var revB = H(11, "anna@northwind.example", "NW-1042 rev B", 5);
    var other = H(12, "tom@contoso.example", "flange-DN100 rev A", 9);
    cache.UpsertHeaders(new[] { revC, revB, other });
    cache.SaveAttachments("acc", "INBOX", 10, new List<AttachmentInfo> { new() { FileName = "flange-DN100-revC.pdf", Part = "2" }, new() { FileName = "flange-DN100-revC.dxf", Part = "3" } });
    cache.SaveAttachments("acc", "INBOX", 11, new List<AttachmentInfo> { new() { FileName = "flange-DN100-revB.pdf", Part = "2" }, new() { FileName = "logo.png", Part = "3" } });
    cache.SaveAttachments("acc", "INBOX", 12, new List<AttachmentInfo> { new() { FileName = "flange-DN100-revA.pdf", Part = "2" } });
    var c = RevisionService.FindCandidates(cache, revC, "flange-DN100-revC.pdf");
    Check(c.Count == 1 && c[0].FileName == "flange-DN100-revB.pdf" && c[0].Revision == "B" && c[0].Uid == 11, $"rev B from Anna's older mail ({string.Join(", ", c.Select(x => x.FileName))})");
    Check(c[0].Header != null && c[0].Attachment != null, "candidate can be downloaded (header + attachment)");
    Check(!c.Any(x => x.FileName.EndsWith(".dxf")), "other file types are not offered");
    Check(!c.Any(x => x.FileName.Contains("revA")), "another sender's file is not offered");
    var none = RevisionService.FindCandidates(cache, revB, "logo.png");
    Check(none.Count == 0, "no candidates for a plain file");
}));

await Test("teklif masası: RFQ detection and field extraction in Turkish and English", () => Sync(() =>
{
    // Turkish RFQ example
    var trSub = "Teklif talebi – 120 adet flanş NW-1042";
    var trBody = """
    Merhabalar,
    Ekteki NW-1042 rev C flanştan 120 adet için fiyat teklifi rica ediyoruz.
    Malzeme: Alüminyum 6061-T6
    Son teklif tarihi: 25.10.2026
    İstenen Teslim: 15.11.2026
    Firma: Schmidt Makina A.Ş.
    """;
    var (trIsRfq, trQuote) = QuoteStore.DetectRfq(trSub, trBody, "Anna Schmidt <anna@schmidt-makina.example>", new[] { "flange-DN100-revC.pdf" });
    Check(trIsRfq, "Turkish RFQ email detected as RFQ");
    Check(trQuote.PartNumber.Contains("1042") || trQuote.PartNumber.Contains("flange"), "TR part number extracted");
    Check(trQuote.Revision == "C", "TR revision extracted");
    Check(trQuote.Quantity == 120, "TR quantity extracted (120)");
    Check(trQuote.Material.Contains("Alüminyum 6061"), "TR material extracted");
    Check(trQuote.Deadline.HasValue && trQuote.Deadline.Value.Day == 25, "TR deadline extracted (25.10.2026)");
    Check(trQuote.RequestedDelivery.HasValue && trQuote.RequestedDelivery.Value.Day == 15, "TR delivery date extracted (15.11.2026)");
    Check(trQuote.Contact.Contains("Anna") || trQuote.Customer.Contains("Schmidt"), "TR contact/customer extracted");

    // English RFQ example
    var enSub = "Quotation Request: bracket-revC";
    var enBody = """
    Dear Sales Team,
    Please provide your quotation for 250 pcs of bracket-revC.
    Material: Stainless Steel 304
    Deadline: 2026-10-24
    Delivery: 2026-11-20
    Company: Atlas Aerospace Ltd.
    """;
    var (enIsRfq, enQuote) = QuoteStore.DetectRfq(enSub, enBody, "Tom Becker <t.becker@atlas-aero.example>", new[] { "bracket-revC.stp" });
    Check(enIsRfq, "English quotation email detected as RFQ");
    Check(enQuote.PartNumber.Contains("bracket"), "EN part number extracted");
    Check(enQuote.Revision == "C", "EN revision extracted");
    Check(enQuote.Quantity == 250, "EN quantity extracted (250)");
    Check(enQuote.Material.Contains("Stainless"), "EN material extracted");
    Check(enQuote.Deadline.HasValue && enQuote.Deadline.Value.Day == 24, "EN deadline extracted");
    Check(enQuote.Customer.Contains("Atlas Aerospace"), "EN company name extracted");

    // Negative case (normal non-quote email)
    var normalSub = "Haftalık proje koordinasyon toplantısı";
    var normalBody = "Toplantı yarın saat 10:00'da odada yapılacaktır. Lütfen hazır bulunun.";
    var (normIsRfq, _) = QuoteStore.DetectRfq(normalSub, normalBody, "Ali Veli <ali@example.com>");
    Check(!normIsRfq, "Normal email is NOT detected as RFQ");
}));

await Test("teklif masası: QuoteStore persistence, CRUD and status transitions", () => Sync(() =>
{
    var qPath = Path.Combine(root, "test-quotes.json");
    var store = new QuoteStore(qPath);
    Check(store.Quotes.Count == 0, "Initial quotes empty");

    var q1 = new QuoteItem
    {
        Customer = "Acme Corp",
        Contact = "John Doe",
        ContactEmail = "john@acme.example",
        PartNumber = "NW-1042",
        Revision = "B",
        Quantity = 500,
        Material = "St52-3",
        Deadline = DateTime.Today.AddDays(3),
        Status = QuoteStatus.New,
        SourceMessageId = "<msg-101@acme.example>"
    };

    var changedFired = false;
    store.Changed += () => changedFired = true;
    store.AddOrUpdate(q1);

    Check(changedFired, "Changed event fired on add");
    Check(store.Quotes.Count == 1, "Quote added to store");

    // Reload from disk
    var reloaded = new QuoteStore(qPath);
    Check(reloaded.Quotes.Count == 1, "Quotes persisted to disk");
    Check(reloaded.Quotes[0].PartNumber == "NW-1042", "Persisted fields preserved");
    Check(reloaded.Quotes[0].Quantity == 500, "Quantity preserved");

    // Update status
    q1.Status = QuoteStatus.InProgress;
    store.AddOrUpdate(q1);
    var reloaded2 = new QuoteStore(qPath);
    Check(reloaded2.Quotes[0].Status == QuoteStatus.InProgress, "Status updated to InProgress");

    // Delete
    var deleted = store.Delete(q1.Id);
    Check(deleted, "Quote deleted");
    Check(store.Quotes.Count == 0, "Quotes list now empty");
}));

await Test("teklif masası: reply with attachment automatically updates quote status to Sent", () => Sync(() =>
{
    var qPath = Path.Combine(root, "test-quotes-auto.json");
    var store = new QuoteStore(qPath);

    var q = new QuoteItem
    {
        Customer = "Siemens Energy",
        Contact = "Hans Weber",
        ContactEmail = "hans.weber@siemens.example",
        PartNumber = "flange-DN100",
        Revision = "C",
        Quantity = 1000,
        Status = QuoteStatus.New,
        SourceMessageId = "<rfq-siemens-999@siemens.example>"
    };
    store.AddOrUpdate(q);

    // 1. Reply with NO attachments -> status should remain New
    var updatedNoAtt = store.CheckSentMail("hans.weber@siemens.example", "<rfq-siemens-999@siemens.example>", "Re: RFQ", attachmentCount: 0);
    Check(!updatedNoAtt, "Sending without attachment does not mark quote Sent");
    Check(store.Get(q.Id)?.Status == QuoteStatus.New, "Quote remains New without attachment");

    // 2. Reply WITH attachment -> status should become Sent
    var updatedWithAtt = store.CheckSentMail("hans.weber@siemens.example", "<rfq-siemens-999@siemens.example>", "Re: RFQ", attachmentCount: 1);
    Check(updatedWithAtt, "Sending with attachment marks quote Sent");
    Check(store.Get(q.Id)?.Status == QuoteStatus.Sent, "Quote status updated to Sent");
}));

await Test("teklif masası: ClosedXML Excel export generation and structure", () => Sync(() =>
{
    var exportPath = Path.Combine(root, "teklif_masasi_export.xlsx");
    if (File.Exists(exportPath)) File.Delete(exportPath);

    var quotes = new List<QuoteItem>
    {
        new()
        {
            Customer = "Schmidt Präzision",
            Contact = "Anna Schmidt",
            PartNumber = "NW-1042",
            Revision = "C",
            Quantity = 120,
            Material = "Alüminyum 6061-T6",
            Deadline = DateTime.Today.AddDays(2),
            RequestedDelivery = DateTime.Today.AddDays(15),
            Status = QuoteStatus.InProgress,
            Notes = "Toleranslar resme uygun"
        },
        new()
        {
            Customer = "Atlas Aero",
            Contact = "Tom Becker",
            PartNumber = "bracket-revC",
            Revision = "C",
            Quantity = 250,
            Material = "7075-T6",
            Deadline = DateTime.Today.AddDays(-1), // Overdue
            Status = QuoteStatus.New,
            Notes = "5 eksen işleme"
        }
    };

    QuoteStore.ExportToExcel(quotes, exportPath);
    Check(File.Exists(exportPath), "Excel export file generated on disk");

    using var wb = new ClosedXML.Excel.XLWorkbook(exportPath);
    Check(wb.Worksheets.Count >= 1, "Workbook contains worksheets");
    var ws = wb.Worksheet("Teklif Masası");
    Check(ws != null, "Worksheet 'Teklif Masası' found");
    Check(ws.Cell(1, 1).GetString() == "Durum", "Header 1 is Durum");
    Check(ws.Cell(1, 2).GetString() == "Parça No", "Header 2 is Parça No");
    Check(ws.Cell(2, 2).GetString() == "NW-1042", "Row 1 Parça No is NW-1042");
    Check(ws.Cell(3, 2).GetString() == "bracket-revC", "Row 2 Parça No is bracket-revC");
}));

await Test("şablonlar: değişken doldurma ve regex çözümleme (TR & EN)", () => Sync(() =>
{
    var ctx = TemplateContext.Create(
        to: "\"Ahmet Yılmaz\" <ahmet.yilmaz@abcmakina.com>",
        subject: "RE: NW-1042 Fiyat Talebi",
        signature: "Caner Akın\nSatış Müdürü",
        date: new DateTime(2026, 10, 20)
    );

    Check(ctx.FirstName == "Ahmet", "FirstName parsed as Ahmet");
    Check(ctx.LastName == "Yılmaz", "LastName parsed as Yılmaz");
    Check(ctx.Company == "Abcmakina", "Company parsed as Abcmakina");
    Check(ctx.PartNumber == "NW-1042", "PartNumber parsed as NW-1042");
    Check(ctx.Date == "20.10.2026", "Date formatted as 20.10.2026");

    // Turkish template rendering
    var trTmpl = "Sayın {Ad} {Soyad} ({Firma}),\n\n{ParçaNo} parçasına ait teklifimiz ekte bilgilerinize sunulmuştur. Tarih: {Tarih}.\n\n{İmza}";
    var trRendered = TemplateEngine.Render(trTmpl, ctx);
    Check(trRendered.Contains("Sayın Ahmet Yılmaz (Abcmakina)"), "TR rendered greeting with Ad, Soyad, Firma");
    Check(trRendered.Contains("NW-1042 parçasına ait teklifimiz"), "TR rendered ParçaNo");
    Check(trRendered.Contains("Tarih: 20.10.2026"), "TR rendered Tarih");
    Check(trRendered.Contains("Caner Akın"), "TR rendered İmza");

    // English template rendering with English variable names and case-insensitivity
    var enCtx = TemplateContext.Create(
        to: "Anna Keller <anna.keller@northwind.example>",
        subject: "Quotation request - bracket-revC",
        signature: "Best regards,\nJohn Doe",
        date: new DateTime(2026, 10, 20)
    );
    Check(enCtx.FirstName == "Anna", "EN FirstName parsed as Anna");
    Check(enCtx.LastName == "Keller", "EN LastName parsed as Keller");
    Check(enCtx.Company == "Northwind", "EN Company parsed as Northwind");
    Check(enCtx.PartNumber == "bracket-revC", "EN PartNumber parsed as bracket-revC");

    var enTmpl = "Dear {firstname} {lastname} ({company}),\n\nPlease find attached quotation for {partnumber}.\nDate: {date}.\n\n{signature}";
    var enRendered = TemplateEngine.Render(enTmpl, enCtx);
    Check(enRendered.Contains("Dear Anna Keller (Northwind)"), "EN rendered greeting case-insensitively");
    Check(enRendered.Contains("quotation for bracket-revC"), "EN rendered partnumber");
    Check(enRendered.Contains("Date: 20.10.2026"), "EN rendered date");
    Check(enRendered.Contains("Best regards,\nJohn Doe"), "EN rendered signature");

    // HTML conversion helper
    var html = TemplateEngine.TextToHtml("Line 1\n\nLine 2");
    Check(html.Contains("<div>Line 1</div>"), "HTML conversion has line 1");
    Check(html.Contains("<div><br></div>"), "HTML conversion has empty line div");
    Check(html.Contains("<div>Line 2</div>"), "HTML conversion has line 2");
}));

await Test("şablonlar: TemplateStore kalıcılık, CRUD, varsayılanlar ve JSON içe/dışa aktarma", () => Sync(() =>
{
    var tStoreDir = Path.Combine(root, "tstore_test");
    if (Directory.Exists(tStoreDir)) Directory.Delete(tStoreDir, true);
    Directory.CreateDirectory(tStoreDir);

    var store = new TemplateStore(tStoreDir);
    Check(store.Templates.Count >= 6, $"Default templates seeded: {store.Templates.Count} >= 6");
    Check(store.Templates.Any(t => t.Title == "Teklifimiz ektedir"), "Has 'Teklifimiz ektedir' template");
    Check(store.Templates.Any(t => t.Title == "Our quotation is attached"), "Has 'Our quotation is attached' template");

    // Save custom template
    var custom = new EmailTemplate
    {
        Id = "custom-test-1",
        Title = "Özel Fason Teklif",
        Subject = "Teklif Talebi Hk.",
        Body = "Sayın {Ad}, fason işleme teklifimiz ektedir.",
        Category = "Fason",
        Language = "tr"
    };
    store.Save(custom);

    var reloadedStore = new TemplateStore(tStoreDir);
    var loaded = reloadedStore.FindById("custom-test-1");
    Check(loaded != null && loaded.Title == "Özel Fason Teklif", "Custom template persisted and reloaded");

    // Export JSON
    var json = reloadedStore.ExportJson();
    Check(!string.IsNullOrEmpty(json) && json.Contains("Özel Fason Teklif"), "ExportJson exports valid JSON");

    // Delete
    Check(reloadedStore.Delete("custom-test-1"), "Template deleted");
    Check(reloadedStore.FindById("custom-test-1") == null, "Deleted template cannot be found");

    // Import JSON
    var importDir = Path.Combine(root, "tstore_import_test");
    Directory.CreateDirectory(importDir);
    var importStore = new TemplateStore(importDir);
    var count = importStore.ImportJson(json);
    Check(count >= 7, $"ImportJson successfully restored {count} templates");
    Check(importStore.FindById("custom-test-1") != null, "Imported custom template found");
}));

await Test("hızlı adımlar: QuickStepStore kalıcılık, varsayılanlar ve kısayol arama", () => Sync(() =>
{
    var qsDir = Path.Combine(root, "qs_test");
    if (Directory.Exists(qsDir)) Directory.Delete(qsDir, true);
    Directory.CreateDirectory(qsDir);

    var store = new QuickStepStore(qsDir);
    Check(store.Steps.Count >= 3, $"Default quick steps seeded: {store.Steps.Count} >= 3");

    var s1 = store.FindByShortcut("Ctrl+Shift+1");
    Check(s1 != null && s1.Name == "Okundu + Arşive taşı", "Ctrl+Shift+1 maps to Okundu + Arşive taşı");

    var s2 = store.FindByShortcut("Ctrl+Shift+2");
    Check(s2 != null && s2.Name == "Şablonla yanıtla + Bayrakla", "Ctrl+Shift+2 maps to Şablonla yanıtla + Bayrakla");

    var s3 = store.FindByShortcut("Ctrl+Shift+3");
    Check(s3 != null && s3.Name == "Görev yap + Taşı", "Ctrl+Shift+3 maps to Görev yap + Taşı");

    // Custom quick step
    var customQs = new QuickStepItem
    {
        Id = "qs-custom-test",
        Name = "Öncelikli Bayrakla",
        Description = "Bayrak ekler ve kategori atar",
        Icon = "\uE7C1",
        Shortcut = "Ctrl+Shift+4",
        Actions = new List<QuickStepAction>
        {
            new() { Type = QuickStepActionType.Flag },
            new() { Type = QuickStepActionType.SetCategory, Parameter = "Öncelikli" }
        }
    };
    store.Save(customQs);

    var reloadedStore = new QuickStepStore(qsDir);
    var loaded = reloadedStore.FindByShortcut("Ctrl+Shift+4");
    Check(loaded != null && loaded.Name == "Öncelikli Bayrakla", "Custom QuickStep persisted with shortcut Ctrl+Shift+4");
    Check(loaded.Actions.Count == 2, "Custom QuickStep has 2 actions");
}));

await Test("hızlı adımlar: eylem simülasyonu ve çoklu adım zinciri", () => Sync(() =>
{
    var taskStoreDir = Path.Combine(root, "qs_tasks_test");
    Directory.CreateDirectory(taskStoreDir);
    var tasks = new MailStudio.Services.Calendar.TaskStore(Path.Combine(taskStoreDir, "tasks.json"));

    var header = new MailHeader
    {
        AccountId = "acc1",
        Folder = "INBOX",
        Uid = 777,
        FromName = "Mehmet Demir",
        FromAddress = "mehmet@demir-celik.example",
        Subject = "Teklif Talebi - DN100 Flanş",
        Preview = "Ekteki teknik resim için teklifinizi bekliyoruz.",
        IsRead = false,
        IsFlagged = false
    };

    var step = new QuickStepItem
    {
        Name = "Okundu + Bayrakla + Görev Yap",
        Actions = new List<QuickStepAction>
        {
            new() { Type = QuickStepActionType.MarkRead },
            new() { Type = QuickStepActionType.Flag },
            new() { Type = QuickStepActionType.CreateTask, Parameter = "Teklif Hazırla" }
        }
    };

    // Simulate action execution sequence
    foreach (var act in step.Actions)
    {
        switch (act.Type)
        {
            case QuickStepActionType.MarkRead:
                header.IsRead = true;
                break;
            case QuickStepActionType.Flag:
                header.IsFlagged = true;
                break;
            case QuickStepActionType.CreateTask:
                var task = new MailStudio.Services.Calendar.TaskItem
                {
                    Title = header.Subject,
                    Category = act.Parameter ?? "",
                    SourceMail = $"{header.AccountId}|{header.Folder}|{header.Uid}",
                    DueDate = DateTime.Today
                };
                tasks.Save(task);
                break;
        }
    }

    Check(header.IsRead, "Header marked as read by QuickStep");
    Check(header.IsFlagged, "Header flagged by QuickStep");

    var createdTask = tasks.FindBySource($"{header.AccountId}|{header.Folder}|{header.Uid}");
    Check(createdTask != null, "Task created in TaskStore by QuickStep");
    Check(createdTask?.Category == "Teklif Hazırla", "Task category set properly");
    Check(createdTask?.Title == "Teklif Talebi - DN100 Flanş", "Task title matches mail subject");
}));

await Test("snooze: SnoozeStore kalıcılık, CRUD, preset zaman hesapları ve filtreleme", () => Sync(() =>
{
    var snoozeFile = Path.Combine(root, "snooze-test.json");
    if (File.Exists(snoozeFile)) File.Delete(snoozeFile);

    var store = new SnoozeStore(snoozeFile);
    Check(store.Items.Count == 0, "SnoozeStore starts empty");

    // Presets
    var fixedNow = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(3)); // Tuesday 10:00
    var eve = SnoozeStore.GetThisEvening(fixedNow);
    Check(eve.Hour == 18 && eve.Date == fixedNow.Date, "GetThisEvening returns today 18:00");

    var tmw = SnoozeStore.GetTomorrowMorning(fixedNow);
    Check(tmw.Hour == 9 && tmw.Date == fixedNow.Date.AddDays(1), "GetTomorrowMorning returns tomorrow 09:00");

    var nxtWk = SnoozeStore.GetNextWeek(fixedNow);
    Check(nxtWk.Hour == 9 && nxtWk.DayOfWeek == DayOfWeek.Monday && nxtWk > fixedNow, "GetNextWeek returns next Monday 09:00");

    var weekend = SnoozeStore.GetThisWeekend(fixedNow);
    Check(weekend.Hour == 9 && weekend.DayOfWeek == DayOfWeek.Saturday, "GetThisWeekend returns Saturday 09:00");

    // Snooze item
    store.Snooze("acc1", "INBOX", 42, tmw, subject: "Teklif Revizyonu", from: "Anna", wasRead: true, restoreAsUnread: true);
    Check(store.IsSnoozed("acc1", "INBOX", 42), "Message 42 is snoozed");
    Check(!store.IsSnoozed("acc1", "INBOX", 43), "Message 43 is not snoozed");
    Check(!store.IsSnoozed("acc1", "Archive", 42), "Different folder is not snoozed");

    var item = store.Get("acc1", "INBOX", 42);
    Check(item != null && item.Subject == "Teklif Revizyonu" && item.RestoreAsUnread, "SnoozeItem retrieved with correct metadata");

    // Persistence reload
    var reloaded = new SnoozeStore(snoozeFile);
    Check(reloaded.Items.Count == 1, "Persisted snooze item reloaded from disk");
    Check(reloaded.IsSnoozed("acc1", "INBOX", 42), "Reloaded store recognizes snoozed email");

    // Unsnooze
    var un = reloaded.Unsnooze("acc1", "INBOX", 42);
    Check(un, "Unsnooze returns true on existing item");
    Check(!reloaded.IsSnoozed("acc1", "INBOX", 42), "Message 42 is no longer snoozed after Unsnooze");
    Check(reloaded.Items.Count == 0, "Store is empty after unsnooze");
}));

await Test("snooze: zaman aşımı (CheckDue), okunmamış olarak geri dönme ve tarih güncelleme", () => Sync(() =>
{
    var snoozeFile = Path.Combine(root, "snooze-due-test.json");
    if (File.Exists(snoozeFile)) File.Delete(snoozeFile);

    var store = new SnoozeStore(snoozeFile);
    var now = DateTimeOffset.Now;
    var future = now.AddHours(2);

    store.Snooze("acc1", "INBOX", 101, future, subject: "Acil Parça Talebi", from: "Tom", wasRead: true, restoreAsUnread: true);

    // Before due
    var notYet = store.CheckDue(now.AddHours(1));
    Check(notYet.Count == 0, "No items due before scheduled time");
    Check(store.IsSnoozed("acc1", "INBOX", 101), "Message remains snoozed before due time");

    // MailCache integration simulation
    var cacheDir = Path.Combine(root, "cache-snooze-test");
    Directory.CreateDirectory(cacheDir);
    var cache = new MailCache(Path.Combine(cacheDir, "mail.db"));
    cache.Init();

    var header = new MailHeader
    {
        AccountId = "acc1",
        Folder = "INBOX",
        Uid = 101,
        Subject = "Acil Parça Talebi",
        FromName = "Tom",
        Date = now.AddDays(-1),
        IsRead = true
    };
    cache.UpsertHeaders(new[] { header });

    // Verify initial state
    var initHeaders = cache.GetHeaders("acc1", "INBOX");
    Check(initHeaders.Count == 1 && initHeaders[0].IsRead, "Cached message is initially read");

    // After due time
    var due = store.CheckDue(future.AddMinutes(5));
    Check(due.Count == 1 && due[0].Uid == 101, "CheckDue returns expired snooze item");
    Check(!store.IsSnoozed("acc1", "INBOX", 101), "Woken message is automatically removed from snoozed list");

    // Apply waking actions as MainWindow does
    foreach (var d in due)
    {
        if (d.RestoreAsUnread)
        {
            cache.SetFlags(d.AccountId, d.Folder, d.Uid, read: false);
        }
        cache.TouchDate(d.AccountId, d.Folder, d.Uid, DateTimeOffset.UtcNow);
    }

    var updatedHeaders = cache.GetHeaders("acc1", "INBOX");
    Check(updatedHeaders.Count == 1, "Message still in cache");
    Check(!updatedHeaders[0].IsRead, "Woken message marked unread in cache");
    Check(updatedHeaders[0].Date > now, "Woken message date touched so it sorts to the top");
}));

await Test("birleşik gelen kutusu: çoklu hesap INBOX birleştirme, tarih sıralaması ve okunmamış sayısı", () => Sync(() =>
{
    var cacheDir = Path.Combine(root, "cache-unified-test");
    Directory.CreateDirectory(cacheDir);
    var cache = new MailCache(Path.Combine(cacheDir, "mail.db"));
    cache.Init();

    var now = DateTimeOffset.Now;

    // Account A: 2 in INBOX (1 unread, 1 read), 1 in Sent
    var m1 = new MailHeader { AccountId = "acc-a", Folder = "INBOX", Uid = 1, Subject = "İş Teklifi A1", Date = now.AddHours(-2), IsRead = true };
    var m2 = new MailHeader { AccountId = "acc-a", Folder = "INBOX", Uid = 2, Subject = "İş Teklifi A2", Date = now.AddHours(-1), IsRead = false };
    var m3 = new MailHeader { AccountId = "acc-a", Folder = "Sent", Uid = 3, Subject = "Giden Cevap A3", Date = now.AddHours(-0.5), IsRead = true };

    // Account B: 2 in INBOX (1 unread, 1 read)
    var m4 = new MailHeader { AccountId = "acc-b", Folder = "INBOX", Uid = 10, Subject = "Tedarikçi B1", Date = now.AddMinutes(-30), IsRead = false };
    var m5 = new MailHeader { AccountId = "acc-b", Folder = "INBOX", Uid = 11, Subject = "Tedarikçi B2", Date = now.AddHours(-4), IsRead = true };

    cache.UpsertHeaders(new[] { m1, m2, m3, m4, m5 });

    var unified = cache.GetUnifiedInboxHeaders();
    Check(unified.Count == 4, $"Unified Inbox returned {unified.Count} mails (expected 4, Sent excluded)");
    Check(unified.All(m => m.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase)), "All unified mails are from INBOX");

    // Verify sorted by date descending: m4 (30m ago), m2 (1h ago), m1 (2h ago), m5 (4h ago)
    Check(unified[0].Uid == 10 && unified[0].AccountId == "acc-b", "Newest mail is first (acc-b, 30m ago)");
    Check(unified[1].Uid == 2 && unified[1].AccountId == "acc-a", "Second mail is acc-a (1h ago)");
    Check(unified[2].Uid == 1 && unified[2].AccountId == "acc-a", "Third mail is acc-a (2h ago)");
    Check(unified[3].Uid == 11 && unified[3].AccountId == "acc-b", "Oldest mail is last (acc-b, 4h ago)");

    // Unread count across both accounts
    var unreadTotal = cache.GetUnifiedInboxUnreadCount();
    Check(unreadTotal == 2, $"Unified inbox unread count is {unreadTotal} (expected 2)");

    // Account color palette helper
    var accA = new AccountConfig { Id = "acc-a", Email = "is@example.com" };
    var accB = new AccountConfig { Id = "acc-b", Email = "kisisel@example.com" };
    var colA = AccountColorHelper.GetAccountColor(accA, 0);
    var colB = AccountColorHelper.GetAccountColor(accB, 1);
    Check(!string.IsNullOrEmpty(colA) && !string.IsNullOrEmpty(colB), "Colors assigned to accounts");
    Check(colA != colB, "Different accounts get distinct colors from palette");
}));

await Test("arama klasörleri: SearchFolderStore kalıcılık, varsayılanlar ve kriter değerlendirme motoru", () => Sync(() =>
{
    var sfFile = Path.Combine(root, "searchfolders-test.json");
    if (File.Exists(sfFile)) File.Delete(sfFile);

    var store = new SearchFolderStore(sfFile);
    Check(store.Folders.Count == 3, $"SearchFolderStore initialized with {store.Folders.Count} default folders (expected 3)");

    var unreadFolder = store.Find("search-unread");
    Check(unreadFolder != null && unreadFolder.Criteria.IsUnread == true, "Default 'Okunmamış' folder has IsUnread=true");

    var flaggedFolder = store.Find("search-flagged");
    Check(flaggedFolder != null && flaggedFolder.Criteria.IsFlagged == true, "Default 'Bayraklı' folder has IsFlagged=true");

    var attFolder = store.Find("search-this-week-attachments");
    Check(attFolder != null && attFolder.Criteria.HasAttachments == true && attFolder.Criteria.DateRangePreset == "this_week",
        "Default 'Bu haftanın ekli mailleri' folder has HasAttachments=true and this_week preset");

    // Add custom search folder
    var custom = new SearchFolderItem
    {
        Id = "sf-custom-pdf",
        Name = "PDF Çizimler",
        Criteria = new SearchFolderCriteria
        {
            HasAttachments = true,
            AttachmentType = "pdf",
            From = "Anna",
            Query = "flange"
        }
    };
    store.AddOrUpdate(custom);
    Check(store.Folders.Count == 4, "Custom search folder added");

    // Reload from disk to verify persistence
    var store2 = new SearchFolderStore(sfFile);
    Check(store2.Folders.Count == 4, "SearchFolderStore persisted to JSON and reloaded");
    var found = store2.Find("sf-custom-pdf");
    Check(found != null && found.Criteria.AttachmentType == "pdf" && found.Criteria.From == "Anna", "Custom criteria persisted");

    // Test Search Criteria against sample mails in MailCache
    var cacheDir = Path.Combine(root, "cache-searchfolder-test");
    Directory.CreateDirectory(cacheDir);
    var cache = new MailCache(Path.Combine(cacheDir, "mail.db"));
    cache.Init();

    var now = DateTimeOffset.Now;
    var startOfWeek = MailStudio.Services.Calendar.Recurrence.WeekStart(now.Date);

    var mailUnread = new MailHeader { AccountId = "demo", Folder = "INBOX", Uid = 1, Subject = "Teklif", FromName = "Anna Keller", Date = now, IsRead = false, HasAttachments = false };
    var mailFlagged = new MailHeader { AccountId = "demo", Folder = "INBOX", Uid = 2, Subject = "Önemli", FromName = "Tom", Date = now, IsRead = true, IsFlagged = true, HasAttachments = false };
    var mailOldAtt = new MailHeader { AccountId = "demo", Folder = "INBOX", Uid = 3, Subject = "Geçen Hafta Çizim", FromName = "Anna", Date = startOfWeek.AddDays(-3), IsRead = true, HasAttachments = true };
    var mailThisWeekAtt = new MailHeader { AccountId = "demo", Folder = "INBOX", Uid = 4, Subject = "Bu Hafta flange-DN100", FromName = "Anna Keller", Date = now, IsRead = false, HasAttachments = true };

    cache.UpsertHeaders(new[] { mailUnread, mailFlagged, mailOldAtt, mailThisWeekAtt });
    cache.SaveAttachments("demo", "INBOX", 4, new List<AttachmentInfo>
    {
        new() { FileName = "flange-DN100-revB.pdf", Part = "1", Size = 2048 }
    });

    // Test Unread search
    var unreadMatches = store.ExecuteSearch(unreadFolder!, cache, now);
    Check(unreadMatches.Count == 2 && unreadMatches.Any(m => m.Uid == 1) && unreadMatches.Any(m => m.Uid == 4), "Unread search folder matches 2 unread mails");

    // Test Flagged search
    var flaggedMatches = store.ExecuteSearch(flaggedFolder!, cache, now);
    Check(flaggedMatches.Count == 1 && flaggedMatches[0].Uid == 2, "Flagged search folder matches only flagged mail");

    // Test This Week's Attachments search
    var weekAttMatches = store.ExecuteSearch(attFolder!, cache, now);
    Check(weekAttMatches.Count == 1 && weekAttMatches[0].Uid == 4, "This week's attachments folder matches mail 4 (mail 3 excluded as last week)");

    // Test Custom PDF search
    var customMatches = store.ExecuteSearch(custom, cache, now);
    Check(customMatches.Count == 1 && customMatches[0].Uid == 4, "Custom PDF search matched mail with PDF attachment, Anna sender, and flange query");

    // Test Delete
    store.Delete("sf-custom-pdf");
    Check(store.Folders.Count == 3, "Custom search folder successfully deleted");
}));

await Test("newsletters: List-Unsubscribe parsing (mailto/http/one-click), mailto URI decomposition, newsletter detection, rule generation, and cache move", async () =>
{
    // 1. Header parsing tests
    var hBoth = "<mailto:unsubscribe@example.com?subject=unsubscribe>, <https://example.com/unsub?id=12345>";
    var parsedBoth = NewsletterHelper.ParseUnsubscribe(hBoth, "List-Unsubscribe=One-Click");
    Check(parsedBoth != null, "parsedBoth not null");
    Check(parsedBoth!.MailtoUri == "mailto:unsubscribe@example.com?subject=unsubscribe", "MailtoUri extracted");
    Check(parsedBoth.HttpUri == "https://example.com/unsub?id=12345", "HttpUri extracted");
    Check(parsedBoth.HasOneClick, "HasOneClick detected");

    var hHttpOnly = "<https://newsletter.example.org/optout>";
    var parsedHttp = NewsletterHelper.ParseUnsubscribe(hHttpOnly);
    Check(parsedHttp != null && parsedHttp.HttpUri == "https://newsletter.example.org/optout" && parsedHttp.MailtoUri == null && !parsedHttp.HasOneClick, "Http only parsed");

    var hMailtoOnly = "<mailto:leave-list@example.com>";
    var parsedMailto = NewsletterHelper.ParseUnsubscribe(hMailtoOnly);
    Check(parsedMailto != null && parsedMailto.MailtoUri == "mailto:leave-list@example.com" && parsedMailto.HttpUri == null, "Mailto only parsed");

    Check(NewsletterHelper.ParseUnsubscribe("") == null, "Empty header returns null");
    Check(NewsletterHelper.ParseUnsubscribe("invalid-without-scheme") == null, "Invalid header returns null");

    // 2. Mailto decomposition tests
    var (to1, sub1, body1) = NewsletterHelper.ParseMailto("mailto:unsub@example.com?subject=Unsubscribe%20Me&body=Please%20remove");
    Check(to1 == "unsub@example.com", "to address extracted");
    Check(sub1 == "Unsubscribe Me", "subject decoded");
    Check(body1 == "Please remove", "body decoded");

    var (to2, sub2, body2) = NewsletterHelper.ParseMailto("mailto:simple-leave@example.com");
    Check(to2 == "simple-leave@example.com" && sub2 == "Unsubscribe", "simple mailto default subject");

    // 3. Newsletter detection
    var m1 = new MailHeader { FromAddress = "news@machining.example", Subject = "Haftalık Bülten", Folder = "INBOX" };
    Check(NewsletterHelper.IsNewsletter(m1), "m1 detected by from + subject");

    var m2 = new MailHeader { FromAddress = "regular.supplier@example.com", Subject = "Teklif", Folder = "Newsletters" };
    Check(NewsletterHelper.IsNewsletter(m2), "m2 detected by folder");

    var m3 = new MailHeader { FromAddress = "supplier@example.com", Subject = "Part order", Folder = "INBOX", ListUnsubscribe = "<https://example.com/unsub>" };
    Check(NewsletterHelper.IsNewsletter(m3), "m3 detected by ListUnsubscribe header");

    var mNormal = new MailHeader { FromAddress = "john.doe@contoso.example", Subject = "Drawing review", Folder = "INBOX" };
    Check(!NewsletterHelper.IsNewsletter(mNormal), "mNormal is not a newsletter");

    // 4. Rule generation
    var rule = NewsletterHelper.CreateMoveToNewslettersRule(m1, "Newsletters");
    Check(rule.Conditions.Any(c => c.Field == "from" && c.Value == "news@machining.example"), "rule has from condition");
    Check(rule.Actions.Any(a => a.Type == "move_to_folder" && a.Value == "Newsletters"), "rule moves to Newsletters");

    // 5. MailCache MoveMessage test
    var cDir = Path.Combine(root, "cache-newsletter-move");
    Directory.CreateDirectory(cDir);
    var cache = new MailCache(Path.Combine(cDir, "mail.db"));
    cache.Init();
    var mailToMove = new MailHeader
    {
        AccountId = "acc1", Folder = "INBOX", Uid = 10,
        Subject = "Bülten", FromName = "News", FromAddress = "news@example.com",
        ListUnsubscribe = "<https://example.com/unsub>"
    };
    cache.UpsertHeaders(new[] { mailToMove });
    cache.SaveBody("acc1", "INBOX", 10, new MailContent { Html = "<p>News</p>", Text = "News", ListUnsubscribe = "<https://example.com/unsub>" });

    var newUid = cache.MoveMessage("acc1", "INBOX", "Newsletters", 10);
    Check(newUid > 0, "MoveMessage allocated new UID in Newsletters folder");
    Check(cache.GetHeaders("acc1", "INBOX").Count == 0, "INBOX message removed");
    var movedList = cache.GetHeaders("acc1", "Newsletters");
    Check(movedList.Count == 1 && movedList[0].Uid == newUid && movedList[0].Folder == "Newsletters" && movedList[0].ListUnsubscribe == "<https://example.com/unsub>", "Moved message intact in Newsletters folder with ListUnsubscribe header");
});

await Test("takvim eşitlemesi: kapalıyken hiçbir ağ isteği yapılmaması", async () =>
{
    var mock = new MockHttpHandler(r => throw new InvalidOperationException("Kapalıyken hiçbir ağ isteği yapılmamalıdır!"));
    var cDir = Path.Combine(root, "cal-disabled-test");
    Directory.CreateDirectory(cDir);
    var store = new CalendarStore(Path.Combine(cDir, "calendar.json"));
    using var sync = new CalendarSyncService(store, mock);

    var acc = new AccountConfig { Id = "acc-disabled", EnableCalendarSync = false, AuthType = AuthType.Microsoft };
    var res = await sync.SyncAccountAsync(acc);
    Check(res.Success, "Sync result success when disabled");
    Check(mock.RequestCount == 0, "Zero HTTP requests executed when calendar sync is disabled");
});

await Test("takvim eşitlemesi: Microsoft Graph iki yönlü eşitleme, çakışma (sunucu kazanır ve not düşülür) ve silme", async () =>
{
    var cDir = Path.Combine(root, "cal-graph-test");
    Directory.CreateDirectory(cDir);
    var store = new CalendarStore(Path.Combine(cDir, "calendar.json"));

    // Server state
    var serverEvents = new List<Dictionary<string, object>>
    {
        new()
        {
            ["id"] = "graph-1",
            ["changeKey"] = "ck-1",
            ["subject"] = "Talaşlı İmalat Koordinasyon",
            ["start"] = new Dictionary<string, string> { ["dateTime"] = "2026-10-21T09:00:00", ["timeZone"] = "UTC" },
            ["end"] = new Dictionary<string, string> { ["dateTime"] = "2026-10-21T10:00:00", ["timeZone"] = "UTC" },
            ["location"] = new Dictionary<string, string> { ["displayName"] = "Toplantı Odası 1" },
            ["isAllDay"] = false,
            ["bodyPreview"] = "Haftalık üretim planı"
        }
    };

    var serverReceivedDeletes = new List<string>();
    var serverReceivedPosts = new List<string>();

    var mock = new MockHttpHandler(req =>
    {
        var uri = req.RequestUri?.ToString() ?? "";
        if (req.Method == HttpMethod.Get && uri.Contains("/me/calendarView"))
        {
            var json = JsonSerializer.Serialize(new { value = serverEvents });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        if (req.Method == HttpMethod.Post && uri.Contains("/me/events"))
        {
            var body = req.Content?.ReadAsStringAsync().Result ?? "";
            serverReceivedPosts.Add(body);
            using (var pd = JsonDocument.Parse(body))
                serverEvents.Add(new Dictionary<string, object>
                {
                    ["id"] = "graph-2", ["changeKey"] = "ck-2", ["subject"] = pd.RootElement.GetProperty("subject").GetString()!,
                    ["start"] = new Dictionary<string, string> { ["dateTime"] = pd.RootElement.GetProperty("start").GetProperty("dateTime").GetString()!, ["timeZone"] = "UTC" },
                    ["end"] = new Dictionary<string, string> { ["dateTime"] = pd.RootElement.GetProperty("end").GetProperty("dateTime").GetString()!, ["timeZone"] = "UTC" },
                    ["isAllDay"] = false
                });
            var resJson = JsonSerializer.Serialize(new { id = "graph-2", changeKey = "ck-2" });
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent(resJson, Encoding.UTF8, "application/json") };
        }
        if (req.Method == HttpMethod.Patch && uri.Contains("/me/events/"))
        {
            var resJson = JsonSerializer.Serialize(new { id = "graph-1", changeKey = "ck-1-updated" });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(resJson, Encoding.UTF8, "application/json") };
        }
        if (req.Method == HttpMethod.Delete && uri.Contains("/me/events/"))
        {
            var id = uri.Substring(uri.LastIndexOf('/') + 1);
            serverReceivedDeletes.Add(id);
            serverEvents.RemoveAll(x => (string)x["id"] == id);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });

    // A local-only event (no account calendar) must never be uploaded
    var privateEv = new CalendarEvent { Title = "Özel – sadece bu bilgisayarda", Start = DateTime.Today.AddHours(8), End = DateTime.Today.AddHours(9) };
    store.Save(privateEv);

    using var sync = new CalendarSyncService(store, mock) { CustomAccessToken = "mock-token" };
    var acc = new AccountConfig { Id = "acc-graph", EnableCalendarSync = true, AuthType = AuthType.Microsoft };

    // 1. Initial read sync: server event imported locally
    var r1 = await sync.SyncAccountAsync(acc);
    Check(r1.Success, "Initial sync succeeded");
    Check(r1.CreatedLocally == 1, "1 event created locally from server");
    Check(r1.CreatedRemote == 0 && serverReceivedPosts.Count == 0, "local-only event was not uploaded");
    Check(store.Events.Count == 2, "CalendarStore has the server event + the local-only one");
    var ev1 = store.FindByExternalId("graph-1");
    Check(ev1 != null && ev1.Title == "Talaşlı İmalat Koordinasyon", "Imported event title correct");
    Check(ev1!.Start == new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc).ToLocalTime(), $"Graph UTC time converted to local once ({ev1.Start})");
    Check(!ev1.SyncDirty, "imported event is not a local change");
    Check(ev1?.ExternalEtag == "ck-1", "Imported event etag saved");

    // 2. Local addition pushed to server
    var newLocal = new CalendarEvent
    {
        AccountId = acc.Id,
        Title = "Kalite Kontrol Denetimi",
        Start = DateTime.Today.AddHours(14),
        End = DateTime.Today.AddHours(15),
        Location = "Ölçüm Laboratuvarı"
    };
    store.Save(newLocal);
    Check(store.Events.Count == 3, "CalendarStore now has 3 events");

    var r2 = await sync.SyncAccountAsync(acc);
    Check(r2.Success, "Second sync succeeded");
    Check(r2.CreatedRemote == 1, "New local event pushed to server");
    Check(serverReceivedPosts.Count == 1, "Server received 1 POST request");
    var reloadedLocal = store.Find(newLocal.Id);
    Check(reloadedLocal?.ExternalId == "graph-2" && reloadedLocal?.ExternalEtag == "ck-2", "New local event received remote ID and Etag");

    // 3. Conflict resolution: both modified -> Server wins and note is made
    ev1!.Title = "Yerel Olarak Değiştirildi";
    ev1.Modified = DateTime.Now;
    store.Save(ev1);

    // Server also changed
    serverEvents[0]["subject"] = "Sunucu Tarafında Değiştirildi";
    serverEvents[0]["changeKey"] = "ck-1-conflict";

    var r3 = await sync.SyncAccountAsync(acc);
    Check(r3.Success, "Conflict sync succeeded");
    Check(r3.ConflictsResolved == 1, "Conflict resolved");
    var confEv = store.FindByExternalId("graph-1");
    Check(confEv?.Title == "Sunucu Tarafında Değiştirildi", "Server won conflict on Title");
    Check(confEv?.Notes.Contains("Çakışma çözüldü") == true, "Conflict resolution note recorded in Notes");

    // 4. Local deletion -> server receives DELETE
    store.Delete(reloadedLocal!);
    Check(store.Events.Count == 2, "Local store has 2 events after deletion");
    Check(store.GetTombstones(acc.Id).Count == 1, "Tombstone created for deleted event");

    var r4 = await sync.SyncAccountAsync(acc);
    Check(r4.Success, "Delete sync succeeded");
    Check(r4.DeletedRemote == 1, "Event deleted on server");
    Check(serverReceivedDeletes.Contains("graph-2"), "Server received DELETE for graph-2");
    Check(store.GetTombstones(acc.Id).Count == 0, "Tombstone removed after successful remote delete");

    // 5. Server deletion -> local event removed
    serverEvents.Clear();
    var r5 = await sync.SyncAccountAsync(acc);
    Check(r5.Success, "Server deletion sync succeeded");
    Check(r5.DeletedLocally == 1, "Event removed locally when deleted on server");
    Check(store.Events.Count == 1 && store.Find(privateEv.Id) != null, "only the local-only event is left");
    Check(serverReceivedPosts.Count == 1, "the local-only event was never uploaded");
});

await Test("takvim eşitlemesi: sayfalı okuma; liste yarım kalırsa yerelde hiçbir şey silinmez", async () =>
{
    var cDir = Path.Combine(root, "cal-paging-test");
    Directory.CreateDirectory(cDir);
    var store = new CalendarStore(Path.Combine(cDir, "calendar.json"));
    object Ev(string id, int day) => new Dictionary<string, object>
    {
        ["id"] = id, ["changeKey"] = "ck-" + id, ["subject"] = "Etkinlik " + id, ["isAllDay"] = false,
        ["start"] = new Dictionary<string, string> { ["dateTime"] = DateTime.Today.AddDays(day).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" },
        ["end"] = new Dictionary<string, string> { ["dateTime"] = DateTime.Today.AddDays(day).AddHours(1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" }
    };
    var failPage2 = false;
    var mock = new MockHttpHandler(req =>
    {
        var uri = req.RequestUri!.ToString();
        if (uri.Contains("page=2"))
            return failPage2 ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { Ev("p2", 2) } }), Encoding.UTF8, "application/json") };
        if (uri.Contains("/me/calendarView"))
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                { ["value"] = new[] { Ev("p1", 1) }, ["@odata.nextLink"] = "https://graph.example.test/v1.0/me/calendarView?page=2" }), Encoding.UTF8, "application/json") };
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    using var sync = new CalendarSyncService(store, mock) { CustomAccessToken = "t" };
    var acc = new AccountConfig { Id = "acc-page", EnableCalendarSync = true, AuthType = AuthType.Microsoft };
    var r1 = await sync.SyncAccountAsync(acc);
    Check(r1.Success && r1.CreatedLocally == 2 && store.Events.Count == 2, $"both pages read ({r1.CreatedLocally})");
    failPage2 = true;
    var r2 = await sync.SyncAccountAsync(acc);
    Check(!r2.Success, "failed page reported as an error");
    Check(store.Events.Count == 2 && r2.DeletedLocally == 0, "nothing deleted locally from an incomplete listing");
});

await Test("takvim eşitlemesi: Google Calendar API v3 iki yönlü eşitleme, silme ve çakışma", async () =>
{
    var cDir = Path.Combine(root, "cal-google-test");
    Directory.CreateDirectory(cDir);
    var store = new CalendarStore(Path.Combine(cDir, "calendar.json"));

    var googleEvents = new List<Dictionary<string, object>>
    {
        new()
        {
            ["id"] = "goo-1",
            ["etag"] = "\"etag-g1\"",
            ["status"] = "confirmed",
            ["summary"] = "Siemens RFQ Toplantısı",
            ["description"] = "Teklif detayları",
            ["start"] = new Dictionary<string, string> { ["dateTime"] = "2026-10-22T11:00:00Z" },
            ["end"] = new Dictionary<string, string> { ["dateTime"] = "2026-10-22T12:00:00Z" },
            ["location"] = "Ofis"
        }
    };

    var serverReceivedDeletes = new List<string>();

    var mock = new MockHttpHandler(req =>
    {
        var uri = req.RequestUri?.ToString() ?? "";
        if (req.Method == HttpMethod.Get && uri.Contains("/calendars/primary/events"))
        {
            var json = JsonSerializer.Serialize(new { items = googleEvents });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        if (req.Method == HttpMethod.Post && uri.Contains("/calendars/primary/events"))
        {
            googleEvents.Add(new Dictionary<string, object>
            {
                ["id"] = "goo-2",
                ["etag"] = "\"etag-g2\"",
                ["status"] = "confirmed",
                ["summary"] = "Fason Sevkiyatı"
            });
            var resJson = JsonSerializer.Serialize(new { id = "goo-2", etag = "\"etag-g2\"" });
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent(resJson, Encoding.UTF8, "application/json") };
        }
        if (req.Method == HttpMethod.Patch && uri.Contains("/calendars/primary/events/"))
        {
            var resJson = JsonSerializer.Serialize(new { id = "goo-1", etag = "\"etag-g1-updated\"" });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(resJson, Encoding.UTF8, "application/json") };
        }
        if (req.Method == HttpMethod.Delete && uri.Contains("/calendars/primary/events/"))
        {
            var id = uri.Substring(uri.LastIndexOf('/') + 1);
            serverReceivedDeletes.Add(id);
            googleEvents.RemoveAll(x => (string)x["id"] == id);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });

    using var sync = new CalendarSyncService(store, mock) { CustomAccessToken = "mock-google-token" };
    var acc = new AccountConfig { Id = "acc-google", EnableCalendarSync = true, AuthType = AuthType.Google };

    // Initial sync
    var r1 = await sync.SyncAccountAsync(acc);
    Check(r1.Success, "Google initial sync succeeded");
    Check(r1.CreatedLocally == 1, "Google event created locally");
    var ev = store.FindByExternalId("goo-1");
    Check(ev != null && ev.Title == "Siemens RFQ Toplantısı", "Google title correct");

    // Local addition
    var localEv = new CalendarEvent { AccountId = acc.Id, Title = "Fason Sevkiyatı", Start = DateTime.Today.AddHours(16), End = DateTime.Today.AddHours(17) };
    store.Save(localEv);
    var r2 = await sync.SyncAccountAsync(acc);
    Check(r2.CreatedRemote == 1, "Google local addition pushed");
    Check(store.Find(localEv.Id)?.ExternalId == "goo-2", "Google remote id assigned");

    // Conflict: both modified
    ev!.Title = "Yerel Google Başlık";
    ev.Modified = DateTime.Now;
    store.Save(ev);
    googleEvents[0]["summary"] = "Sunucu Google Başlık";
    googleEvents[0]["etag"] = "\"etag-conflict\"";

    var r3 = await sync.SyncAccountAsync(acc);
    Check(r3.ConflictsResolved == 1, "Google conflict resolved");
    var conf = store.FindByExternalId("goo-1");
    Check(conf?.Title == "Sunucu Google Başlık", "Google conflict server won");
    Check(conf?.Notes.Contains("Çakışma çözüldü") == true, "Google conflict note present");

    // Deletion
    store.Delete(store.Find(localEv.Id)!);
    var r4 = await sync.SyncAccountAsync(acc);
    Check(r4.DeletedRemote == 1 && serverReceivedDeletes.Contains("goo-2"), "Google remote delete executed");
});

await Test("takvim eşitlemesi: CalDAV iki yönlü eşitleme, XML REPORT ayrıştırma, VEVENT PUT/DELETE ve çakışma", async () =>
{
    var cDir = Path.Combine(root, "cal-caldav-test");
    Directory.CreateDirectory(cDir);
    var store = new CalendarStore(Path.Combine(cDir, "calendar.json"));

    var caldavVevent = @"BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//MailStudio//Test//EN
BEGIN:VEVENT
UID:caldav-uid-100@test
SUMMARY:CalDAV Bakım Randevusu
DTSTART:20261023T080000Z
DTEND:20261023T090000Z
LOCATION:Atölye 2
DESCRIPTION:Periyodik CNC bakım
END:VEVENT
END:VCALENDAR";

    var xmlResponse = $@"<D:multistatus xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"">
  <D:response>
    <D:href>/caldav/events/caldav-100.ics</D:href>
    <D:propstat>
      <D:prop>
        <D:getetag>""etag-caldav-1""</D:getetag>
        <C:calendar-data>{caldavVevent}</C:calendar-data>
      </D:prop>
      <D:status>HTTP/1.1 200 OK</D:status>
    </D:propstat>
  </D:response>
</D:multistatus>";

    var serverReceivedPuts = new List<string>();
    var serverReceivedDeletes = new List<string>();

    var mock = new MockHttpHandler(req =>
    {
        var uri = req.RequestUri?.ToString() ?? "";
        if (req.Method.Method == "REPORT" && uri.Contains("/caldav/"))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(xmlResponse, Encoding.UTF8, "application/xml") };
        }
        if (req.Method == HttpMethod.Put && uri.Contains("/caldav/"))
        {
            serverReceivedPuts.Add(uri);
            var res = new HttpResponseMessage(System.Net.HttpStatusCode.Created);
            res.Headers.ETag = new EntityTagHeaderValue("\"etag-caldav-created\"");
            return res;
        }
        if (req.Method == HttpMethod.Delete && uri.Contains("/caldav/"))
        {
            serverReceivedDeletes.Add(uri);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });

    using var sync = new CalendarSyncService(store, mock) { CustomAccessToken = "mock-caldav-auth" };
    var acc = new AccountConfig
    {
        Id = "acc-caldav",
        EnableCalendarSync = true,
        AuthType = AuthType.Password,
        CalDavUrl = "https://caldav.example.test/caldav/events",
        CalDavUser = "testuser"
    };

    // 1. Initial sync: XML REPORT parsed, local event created
    var r1 = await sync.SyncAccountAsync(acc);
    Check(r1.Success, "CalDAV initial sync succeeded");
    Check(r1.CreatedLocally == 1, "CalDAV event created locally from VEVENT");
    var ev = store.FindByExternalId("/caldav/events/caldav-100.ics");
    Check(ev != null && ev.Title == "CalDAV Bakım Randevusu", "CalDAV event summary parsed properly");
    Check(ev?.ExternalEtag == "\"etag-caldav-1\"", "CalDAV event etag saved");

    // 2. Local addition -> CalDAV PUT
    var localEv = new CalendarEvent { AccountId = acc.Id, Title = "Takım Bileme", Start = DateTime.Today.AddHours(10), End = DateTime.Today.AddHours(11) };
    store.Save(localEv);
    var r2 = await sync.SyncAccountAsync(acc);
    Check(r2.CreatedRemote == 1, "CalDAV local event pushed via PUT");
    Check(serverReceivedPuts.Count == 1, "CalDAV server received 1 PUT");

    // 3. Local deletion -> CalDAV DELETE
    var createdEv = store.Find(localEv.Id);
    Check(createdEv?.ExternalId != null, "Created event has CalDAV ExternalId");
    store.Delete(createdEv!);
    var r3 = await sync.SyncAccountAsync(acc);
    Check(r3.DeletedRemote == 1, "CalDAV remote event deleted");
    Check(serverReceivedDeletes.Count == 1, "CalDAV server received 1 DELETE");
});

await Test("büyük ek uyarısı: AttachmentCompressor 20 MB eşiği ve ZIP arşiv oluşturma", () => Sync(() =>
{
    var zipTestDir = Path.Combine(root, "zip-test");
    Directory.CreateDirectory(zipTestDir);
    var f1 = Path.Combine(zipTestDir, "part1.bin");
    var f2 = Path.Combine(zipTestDir, "part2.bin");

    // Small files check
    File.WriteAllBytes(f1, new byte[1024 * 100]); // 100 KB
    Check(!AttachmentCompressor.IsLarge(new[] { f1 }, out var smallSize), "100 KB file is not large (<20 MB)");
    Check(smallSize == 1024 * 100, "Small size calculated correctly");

    // Large files check (>20 MB total)
    // 11 MB + 11 MB = 22 MB
    File.WriteAllBytes(f1, new byte[11 * 1024 * 1024]);
    File.WriteAllBytes(f2, new byte[11 * 1024 * 1024]);

    Check(AttachmentCompressor.IsLarge(new[] { f1, f2 }, out var totalSize), "22 MB files set detected as large (>20 MB)");
    Check(totalSize >= 22 * 1024 * 1024, "Total size is >= 22 MB");

    // Compress to ZIP
    var zipOut = Path.Combine(zipTestDir, "compressed_parts.zip");
    var resultZip = AttachmentCompressor.CreateZipArchive(new[] { f1, f2 }, zipOut);
    Check(File.Exists(resultZip), "ZIP file created successfully on disk");

    using (var archive = System.IO.Compression.ZipFile.OpenRead(resultZip))
    {
        Check(archive.Entries.Count == 2, $"ZIP archive contains 2 entries (found {archive.Entries.Count})");
        Check(archive.Entries.Any(e => e.Name == "part1.bin"), "ZIP contains part1.bin");
        Check(archive.Entries.Any(e => e.Name == "part2.bin"), "ZIP contains part2.bin");
    }
}));

await Test("kişi kartı: MailCache kişi mailleri ve ekleri sorgulama", () => Sync(() =>
{
    var cDir = Path.Combine(root, "contact-cache-test");
    Directory.CreateDirectory(cDir);
    var cache = new MailCache(Path.Combine(cDir, "mail.db"));
    cache.Init();

    var targetEmail = "anna.keller@northwind.example";
    var otherEmail = "other@example.com";

    var m1 = new MailHeader
    {
        AccountId = "acc1", Folder = "INBOX", Uid = 501,
        FromAddress = targetEmail, FromName = "Anna Keller",
        Subject = "DN100 Flanş Çizimi",
        Date = DateTimeOffset.Now.AddDays(-2),
        HasAttachments = true
    };
    var m2 = new MailHeader
    {
        AccountId = "acc1", Folder = "INBOX", Uid = 502,
        FromAddress = "sales@company.com", To = $"Anna Keller <{targetEmail}>",
        Subject = "Teklifimiz Ektedir",
        Date = DateTimeOffset.Now.AddDays(-1),
        HasAttachments = true
    };
    var mOther = new MailHeader
    {
        AccountId = "acc1", Folder = "INBOX", Uid = 503,
        FromAddress = otherEmail, To = "someone@else.com",
        Subject = "Alakasız Posta",
        Date = DateTimeOffset.Now,
        HasAttachments = false
    };

    cache.UpsertHeaders(new[] { m1, m2, mOther });
    cache.SaveAttachments("acc1", "INBOX", 501, new List<AttachmentInfo>
    {
        new() { FileName = "flange-DN100.dxf", Part = "2", Size = 512000 }
    });
    cache.SaveAttachments("acc1", "INBOX", 502, new List<AttachmentInfo>
    {
        new() { FileName = "teklif-NW1042.pdf", Part = "1", Size = 124000 }
    });

    // 1. Get mails for contact
    var contactMails = cache.GetMailsForContact(targetEmail);
    Check(contactMails.Count == 2, $"GetMailsForContact returned {contactMails.Count} messages (expected 2)");
    Check(contactMails.Any(m => m.Uid == 501) && contactMails.Any(m => m.Uid == 502), "Both sent and received mails for contact returned");
    Check(!contactMails.Any(m => m.Uid == 503), "Unrelated mail not returned");

    // 2. Get attachments for contact
    var contactAtts = cache.GetAttachmentsForContact(targetEmail);
    Check(contactAtts.Count == 2, $"GetAttachmentsForContact returned {contactAtts.Count} attachments (expected 2)");
    Check(contactAtts.Any(a => a.FileName == "flange-DN100.dxf" && a.Uid == 501), "flange-DN100.dxf found for mail 501");
    Check(contactAtts.Any(a => a.FileName == "teklif-NW1042.pdf" && a.Uid == 502), "teklif-NW1042.pdf found for mail 502");
}));

await Test("@ ile bahsetme: ContactBook önerileri ve arama eşleme", () => Sync(() =>
{
    var cDir = Path.Combine(root, "contact-suggest-test");
    Directory.CreateDirectory(cDir);
    var store = new ContactStore(Path.Combine(cDir, "contacts.json"));

    store.Save(new Contact { FirstName = "Anna", LastName = "Lindqvist", Email = "anna@contoso-aero.example", Company = "Contoso Aero" });
    store.Save(new Contact { FirstName = "Mustafa", LastName = "Yılmaz", Email = "mustafa@abcmakina.com", Company = "ABC Makina" });
    store.Save(new Contact { FirstName = "Hans", LastName = "Weber", Email = "hans.weber@siemens.example", Company = "Siemens Energy" });

    var book = new ContactBook(null, store);

    // Query "Anna"
    var annaMatches = book.Suggest("Anna").ToList();
    Check(annaMatches.Count >= 1, "Suggest('Anna') found at least 1 match");
    Check(annaMatches[0].Name == "Anna Lindqvist" && annaMatches[0].Address == "anna@contoso-aero.example", "Anna match details correct");

    // Query "mustafa" (case-insensitive)
    var mustafaMatches = book.Suggest("mustafa").ToList();
    Check(mustafaMatches.Count >= 1 && mustafaMatches[0].Address == "mustafa@abcmakina.com", "Suggest('mustafa') found match");

    // Query "siemens" (company match)
    var siemensMatches = book.Suggest("siemens").ToList();
    Check(siemensMatches.Count >= 1 && siemensMatches[0].Address == "hans.weber@siemens.example", "Suggest('siemens') found match by company");
}));

Console.WriteLine($"RESULT {passed} passed. Synthetic evidence: {root}");

sealed class MockHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; }
    public int RequestCount { get; private set; }

    public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => Handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(Handler(request));
    }
}


