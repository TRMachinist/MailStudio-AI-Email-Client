using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Regression;
using MimeKit;
using System.Text;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "MailStudio-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
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
Console.WriteLine($"RESULT {passed} passed. Synthetic evidence: {root}");
