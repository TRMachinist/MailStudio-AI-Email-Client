using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MailStudio.Models;

namespace MailStudio.Services.Calendar;

/// <summary>Summary of a calendar synchronization run across one or more accounts.</summary>
public sealed class CalendarSyncResult
{
    public bool Success { get; set; } = true;
    public int Fetched { get; set; }
    public int CreatedLocally { get; set; }
    public int UpdatedLocally { get; set; }
    public int DeletedLocally { get; set; }
    public int CreatedRemote { get; set; }
    public int UpdatedRemote { get; set; }
    public int DeletedRemote { get; set; }
    public int ConflictsResolved { get; set; }
    public int Skipped { get; set; }
    public string? Error { get; set; }
    public string? StatusMessage { get; set; }

    public void Merge(CalendarSyncResult other)
    {
        if (!other.Success) Success = false;
        Fetched += other.Fetched;
        CreatedLocally += other.CreatedLocally;
        UpdatedLocally += other.UpdatedLocally;
        DeletedLocally += other.DeletedLocally;
        CreatedRemote += other.CreatedRemote;
        UpdatedRemote += other.UpdatedRemote;
        DeletedRemote += other.DeletedRemote;
        ConflictsResolved += other.ConflictsResolved;
        Skipped += other.Skipped;
        if (!string.IsNullOrEmpty(other.Error))
            Error = string.IsNullOrEmpty(Error) ? other.Error : Error + "; " + other.Error;
    }
}

/// <summary>
/// Two-way calendar sync with Microsoft Graph (Outlook.com / Microsoft 365), Google Calendar API v3 and CalDAV.
/// Safety rules:
/// <list type="bullet">
/// <item>Off by default per account; when off, no network request is made at all.</item>
/// <item>Only a time window (60 days back, 400 days ahead) is read, with every page; if the listing is incomplete nothing is deleted locally.</item>
/// <item>Only events the user put in that account's calendar (<see cref="CalendarEvent.AccountId"/>) are uploaded; local-only events never leave the computer.</item>
/// <item>Conflict (changed on both sides): the server wins and the local version is kept in the notes.</item>
/// </list>
/// </summary>
public class CalendarSyncService : IDisposable
{
    readonly CalendarStore _calendar;
    readonly OAuthService? _oauth;
    readonly HttpClient _http;
    readonly bool _ownsHttpClient;

    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public string GoogleBaseUrl { get; set; } = "https://www.googleapis.com/calendar/v3";
    /// <summary>Test hook: bearer token used instead of OAuth.</summary>
    public string? CustomAccessToken { get; set; }
    public int DaysBack { get; set; } = 60;
    public int DaysAhead { get; set; } = 400;

    public CalendarSyncService(CalendarStore calendar, OAuthService? oauth = null, HttpClient? httpClient = null)
    {
        _calendar = calendar;
        _oauth = oauth;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _ownsHttpClient = httpClient == null;
    }

    public CalendarSyncService(CalendarStore calendar, HttpMessageHandler handler)
        : this(calendar, null, new HttpClient(handler)) => _ownsHttpClient = true;

    public void Dispose() { if (_ownsHttpClient) _http.Dispose(); }

    /// <summary>Synchronizes all accounts that have calendar sync turned on, one after another.</summary>
    public async Task<CalendarSyncResult> SyncAllAccountsAsync(IEnumerable<AccountConfig> accounts, CancellationToken ct = default)
    {
        var total = new CalendarSyncResult();
        foreach (var acc in accounts.Where(a => a.EnableCalendarSync).ToList())
            total.Merge(await SyncAccountAsync(acc, ct));
        return total;
    }

    /// <summary>Synchronizes one account. Makes no network call when sync is off for it.</summary>
    public async Task<CalendarSyncResult> SyncAccountAsync(AccountConfig account, CancellationToken ct = default)
    {
        if (!account.EnableCalendarSync)
            return new CalendarSyncResult { StatusMessage = L.T("Takvim eşitlemesi bu hesap için kapalı.") };
        try
        {
            IProvider provider = account.AuthType switch
            {
                AuthType.Microsoft => new GraphProvider(this, await TokenAsync(account, ct)),
                AuthType.Google => new GoogleProvider(this, await TokenAsync(account, ct)),
                _ => new CalDavProvider(this, account)
            };
            var r = await RunAsync(account, provider, ct);
            account.LastCalendarSyncUtc = DateTime.UtcNow;
            account.CalendarSyncStatus = r.Skipped > 0
                ? L.F("Takvim eşitlemesi tamamlandı ({0} etkinlik atlandı).", r.Skipped)
                : L.T("Takvim eşitlemesi tamamlandı.");
            r.StatusMessage = account.CalendarSyncStatus;
            return r;
        }
        catch (OperationCanceledException) { throw; }
        catch (SyncAuthException ex)
        {
            account.CalendarSyncStatus = L.T("Takvim yetkilendirmesi gerekli. Lütfen hesap ayarlarından oturumu yenileyin.");
            return new CalendarSyncResult { Success = false, Error = ex.Message, StatusMessage = account.CalendarSyncStatus };
        }
        catch (Exception ex)
        {
            account.CalendarSyncStatus = L.T("Takvim eşitleme hatası: ") + ex.Message;
            return new CalendarSyncResult { Success = false, Error = ex.Message, StatusMessage = account.CalendarSyncStatus };
        }
    }

    async Task<string> TokenAsync(AccountConfig account, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(CustomAccessToken)) return CustomAccessToken;
        if (_oauth == null) throw new InvalidOperationException(L.T("OAuth servisi bulunamadı."));
        try { return await _oauth.GetCalendarAccessTokenAsync(account, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new SyncAuthException(ex.Message); }
    }

    // ------------------------------------------------------------------ the merge (provider independent)

    async Task<CalendarSyncResult> RunAsync(AccountConfig account, IProvider p, CancellationToken ct)
    {
        var result = new CalendarSyncResult();
        var from = DateTime.Today.AddDays(-DaysBack);
        var to = DateTime.Today.AddDays(DaysAhead);

        // 1. read the server (all pages of the window)
        var (remote, complete) = await p.ListAsync(from, to, ct);
        result.Fetched = remote.Count;
        var tombs = _calendar.GetTombstones(account.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in remote)
        {
            seen.Add(r.Id);
            var local = _calendar.Events.FirstOrDefault(x => x.AccountId == account.Id && x.ExternalId == r.Id);
            if (local == null)
            {
                if (tombs.Any(t => t.ExternalId == r.Id)) continue;   // deleted here, the delete is pushed below
                var ev = r.Event ?? new CalendarEvent();
                ev.AccountId = account.Id;
                ev.ExternalId = r.Id;
                ev.ExternalEtag = r.Etag;
                Apply(ev, r);
                _calendar.SaveSynced(ev);
                result.CreatedLocally++;
                continue;
            }
            var remoteChanged = !string.Equals(local.ExternalEtag, r.Etag, StringComparison.Ordinal);
            if (!remoteChanged) continue;
            if (local.SyncDirty)
            {
                var note = L.F("[Çakışma çözüldü: Sunucu sürümü uygulandı. Yerel: {0} ({1:g})]", local.Title, local.Start);
                Apply(local, r);
                local.Notes = string.IsNullOrWhiteSpace(local.Notes) ? note : local.Notes + "\n" + note;
                result.ConflictsResolved++;
            }
            else Apply(local, r);
            local.ExternalEtag = r.Etag;
            _calendar.SaveSynced(local);
            result.UpdatedLocally++;
        }

        // 2. deleted on the server: only when the whole window was read, and never an unsent local change
        if (complete)
        {
            foreach (var lev in _calendar.Events.Where(x => x.AccountId == account.Id && !string.IsNullOrEmpty(x.ExternalId)
                                                             && !x.SyncDirty && x.Start >= from && x.Start < to).ToList())
            {
                if (seen.Contains(lev.ExternalId!)) continue;
                _calendar.DeleteSilently(lev.Id);
                result.DeletedLocally++;
            }
        }

        // 3. push local deletions
        var done = new List<string>();
        foreach (var t in tombs)
        {
            if (string.IsNullOrEmpty(t.ExternalId) || await p.DeleteAsync(t.ExternalId, ct))
            {
                done.Add(t.Id);
                if (!string.IsNullOrEmpty(t.ExternalId)) result.DeletedRemote++;
            }
        }
        if (done.Count > 0) _calendar.RemoveTombstones(account.Id, done);

        // 4. push new and changed events of this account's calendar
        foreach (var ev in _calendar.Events.Where(x => x.AccountId == account.Id && (string.IsNullOrEmpty(x.ExternalId) || x.SyncDirty)).ToList())
        {
            if (!p.CanWrite(ev)) { result.Skipped++; continue; }
            if (string.IsNullOrEmpty(ev.ExternalId))
            {
                var (id, etag) = await p.CreateAsync(ev, ct);
                if (id == null) continue;
                ev.ExternalId = id; ev.ExternalEtag = etag;
                _calendar.SaveSynced(ev);
                result.CreatedRemote++;
            }
            else
            {
                var etag = await p.UpdateAsync(ev, ct);
                if (etag == null) continue;
                ev.ExternalEtag = etag;
                _calendar.SaveSynced(ev);
                result.UpdatedRemote++;
            }
        }
        return result;
    }

    static void Apply(CalendarEvent local, RemoteEvent r)
    {
        local.Title = r.Title;
        local.Notes = r.Notes;
        local.Location = r.Location;
        local.Start = r.Start;
        local.End = r.End;
        local.AllDay = r.AllDay;
        if (r.Event != null && !ReferenceEquals(r.Event, local)) local.Repeat = r.Event.Repeat;
    }

    sealed class SyncAuthException(string message) : Exception(message);

    sealed record RemoteEvent(string Id, string? Etag, string Title, string Notes, string Location, DateTime Start, DateTime End, bool AllDay)
    {
        /// <summary>CalDAV: the full parsed event (keeps recurrence).</summary>
        public CalendarEvent? Event { get; init; }
    }

    interface IProvider
    {
        Task<(List<RemoteEvent> Events, bool Complete)> ListAsync(DateTime from, DateTime to, CancellationToken ct);
        bool CanWrite(CalendarEvent e);
        Task<(string? Id, string? Etag)> CreateAsync(CalendarEvent e, CancellationToken ct);
        Task<string?> UpdateAsync(CalendarEvent e, CancellationToken ct);
        Task<bool> DeleteAsync(string externalId, CancellationToken ct);
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var res = await _http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SyncAuthException(((int)res.StatusCode).ToString(CultureInfo.InvariantCulture));
        return res;
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static string Iso(DateTime local) => local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ Microsoft Graph

    sealed class GraphProvider(CalendarSyncService s, string token) : IProvider
    {
        string Base => s.GraphBaseUrl.TrimEnd('/');

        HttpRequestMessage Req(HttpMethod m, string url)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            r.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\", outlook.body-content-type=\"text\"");
            return r;
        }

        public async Task<(List<RemoteEvent>, bool)> ListAsync(DateTime from, DateTime to, CancellationToken ct)
        {
            var list = new List<RemoteEvent>();
            string? url = $"{Base}/me/calendarView?startDateTime={Iso(from)}&endDateTime={Iso(to)}&$top=200";
            for (var page = 0; url != null; page++)
            {
                if (page >= 100) return (list, false);
                using var res = await s.SendAsync(Req(HttpMethod.Get, url), ct);
                if (!res.IsSuccessStatusCode) throw new InvalidOperationException(L.F("Microsoft Graph hatası: {0}", (int)res.StatusCode));
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var ev in arr.EnumerateArray())
                    {
                        var id = Str(ev, "id");
                        if (id.Length == 0 || ev.TryGetProperty("isCancelled", out var c) && c.ValueKind == JsonValueKind.True) continue;
                        var allDay = ev.TryGetProperty("isAllDay", out var ad) && ad.ValueKind == JsonValueKind.True;
                        var start = Time(ev, "start", allDay) ?? DateTime.Now;
                        var end = Time(ev, "end", allDay) ?? start.AddHours(1);
                        var notes = ev.TryGetProperty("body", out var b) ? Str(b, "content") : Str(ev, "bodyPreview");
                        if (notes.Length == 0) notes = Str(ev, "bodyPreview");
                        var loc = ev.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object ? Str(l, "displayName") : "";
                        list.Add(new RemoteEvent(id, Str(ev, "changeKey"), Str(ev, "subject"), notes.Trim(), loc, start, end, allDay));
                    }
                url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }
            return (list, true);
        }

        /// <summary>Graph returns times in UTC (Prefer header) without a 'Z'; all-day events are plain dates.</summary>
        static DateTime? Time(JsonElement ev, string name, bool allDay)
        {
            if (!ev.TryGetProperty(name, out var t)) return null;
            var text = Str(t, "dateTime");
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) return null;
            if (allDay) return DateTime.SpecifyKind(utc.Date, DateTimeKind.Unspecified);
            var tz = Str(t, "timeZone");
            if (tz.Length > 0 && !tz.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            {
                // A server that ignored the Prefer header: the time is in the named zone
                var zone = Ics.FindZone(tz);
                if (zone != null) return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local);
            }
            return utc.ToLocalTime();
        }

        public bool CanWrite(CalendarEvent e) => e.Repeat == null;   // series are edited in Outlook; occurrences from calendarView are single events

        object Payload(CalendarEvent e) => new
        {
            subject = e.Title,
            body = new { contentType = "Text", content = e.Notes ?? "" },
            start = e.AllDay ? new { dateTime = e.Start.Date.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture), timeZone = "UTC" }
                             : new { dateTime = e.Start.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
            end = e.AllDay ? new { dateTime = (e.End.Date > e.Start.Date ? e.End.Date : e.Start.Date.AddDays(1)).ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture), timeZone = "UTC" }
                           : new { dateTime = e.End.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
            location = new { displayName = e.Location ?? "" },
            isAllDay = e.AllDay
        };

        public async Task<(string?, string?)> CreateAsync(CalendarEvent e, CancellationToken ct)
        {
            var req = Req(HttpMethod.Post, $"{Base}/me/events");
            req.Content = new StringContent(JsonSerializer.Serialize(Payload(e)), Encoding.UTF8, "application/json");
            using var res = await s.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return (Str(doc.RootElement, "id"), Str(doc.RootElement, "changeKey"));
        }

        public async Task<string?> UpdateAsync(CalendarEvent e, CancellationToken ct)
        {
            var req = Req(HttpMethod.Patch, $"{Base}/me/events/{Uri.EscapeDataString(e.ExternalId!)}");
            req.Content = new StringContent(JsonSerializer.Serialize(Payload(e)), Encoding.UTF8, "application/json");
            using var res = await s.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return Str(doc.RootElement, "changeKey");
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct)
        {
            using var res = await s.SendAsync(Req(HttpMethod.Delete, $"{Base}/me/events/{Uri.EscapeDataString(id)}"), ct);
            return res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotFound;
        }
    }

    // ------------------------------------------------------------------ Google Calendar

    sealed class GoogleProvider(CalendarSyncService s, string token) : IProvider
    {
        string Base => s.GoogleBaseUrl.TrimEnd('/') + "/calendars/primary/events";

        HttpRequestMessage Req(HttpMethod m, string url)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return r;
        }

        public async Task<(List<RemoteEvent>, bool)> ListAsync(DateTime from, DateTime to, CancellationToken ct)
        {
            var list = new List<RemoteEvent>();
            string? pageToken = null;
            for (var page = 0; ; page++)
            {
                if (page >= 100) return (list, false);
                var url = $"{Base}?singleEvents=true&maxResults=250&timeMin={Uri.EscapeDataString(Iso(from))}&timeMax={Uri.EscapeDataString(Iso(to))}"
                          + (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : "");
                using var res = await s.SendAsync(Req(HttpMethod.Get, url), ct);
                if (!res.IsSuccessStatusCode) throw new InvalidOperationException(L.F("Google Calendar hatası: {0}", (int)res.StatusCode));
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var ev in arr.EnumerateArray())
                    {
                        var id = Str(ev, "id");
                        if (id.Length == 0 || Str(ev, "status") == "cancelled") continue;
                        var (start, allDay) = Time(ev, "start");
                        var (end, _) = Time(ev, "end");
                        var st = start ?? DateTime.Now;
                        list.Add(new RemoteEvent(id, Str(ev, "etag"), Str(ev, "summary"), Str(ev, "description"), Str(ev, "location"),
                            st, end ?? st.AddHours(1), allDay));
                    }
                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(pageToken)) return (list, true);
            }
        }

        static (DateTime?, bool) Time(JsonElement ev, string name)
        {
            if (!ev.TryGetProperty(name, out var t)) return (null, false);
            var date = Str(t, "date");
            if (date.Length > 0 && DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return (d, true);
            var dt = Str(t, "dateTime");
            if (DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o)) return (o.LocalDateTime, false);
            return (null, false);
        }

        public bool CanWrite(CalendarEvent e) => e.Repeat == null;

        object Payload(CalendarEvent e) => e.AllDay
            ? new
            {
                summary = e.Title, description = e.Notes ?? "", location = e.Location ?? "",
                start = (object)new { date = e.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                end = (object)new { date = (e.End.Date > e.Start.Date ? e.End.Date : e.Start.Date.AddDays(1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            }
            : new
            {
                summary = e.Title, description = e.Notes ?? "", location = e.Location ?? "",
                start = (object)new { dateTime = Iso(e.Start) },
                end = (object)new { dateTime = Iso(e.End) }
            };

        public async Task<(string?, string?)> CreateAsync(CalendarEvent e, CancellationToken ct)
        {
            var req = Req(HttpMethod.Post, Base);
            req.Content = new StringContent(JsonSerializer.Serialize(Payload(e)), Encoding.UTF8, "application/json");
            using var res = await s.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return (Str(doc.RootElement, "id"), Str(doc.RootElement, "etag"));
        }

        public async Task<string?> UpdateAsync(CalendarEvent e, CancellationToken ct)
        {
            var req = Req(HttpMethod.Patch, $"{Base}/{Uri.EscapeDataString(e.ExternalId!)}");
            req.Content = new StringContent(JsonSerializer.Serialize(Payload(e)), Encoding.UTF8, "application/json");
            using var res = await s.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return Str(doc.RootElement, "etag");
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct)
        {
            using var res = await s.SendAsync(Req(HttpMethod.Delete, $"{Base}/{Uri.EscapeDataString(id)}"), ct);
            return res.IsSuccessStatusCode || res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
        }
    }

    // ------------------------------------------------------------------ CalDAV (RFC 4791)

    sealed class CalDavProvider : IProvider
    {
        readonly CalendarSyncService _s;
        readonly string _url;
        readonly AuthenticationHeaderValue _auth;

        public CalDavProvider(CalendarSyncService s, AccountConfig a)
        {
            _s = s;
            // The address must be entered by the user: the mail password is never sent to a guessed server.
            if (string.IsNullOrWhiteSpace(a.CalDavUrl) || !a.CalDavUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("CalDAV adresi girilmemiş (https://...). Hesap ayarlarındaki takvim bölümüne yaz."));
            _url = a.CalDavUrl.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(s.CustomAccessToken)) _auth = new AuthenticationHeaderValue("Bearer", s.CustomAccessToken);
            else
            {
                var user = !string.IsNullOrWhiteSpace(a.CalDavUser) ? a.CalDavUser : !string.IsNullOrWhiteSpace(a.UserName) ? a.UserName : a.Email;
                var pass = AccountStore.Unprotect(a.ProtectedPassword);
                _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
            }
        }

        HttpRequestMessage Req(HttpMethod m, string url)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Authorization = _auth;
            return r;
        }

        string Abs(string href) => href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href
            : href.StartsWith('/') ? new Uri(new Uri(_url), href).ToString() : $"{_url}/{href}";

        public async Task<(List<RemoteEvent>, bool)> ListAsync(DateTime from, DateTime to, CancellationToken ct)
        {
            static string T(DateTime d) => d.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var query = $@"<C:calendar-query xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"">
  <D:prop><D:getetag/><C:calendar-data/></D:prop>
  <C:filter><C:comp-filter name=""VCALENDAR""><C:comp-filter name=""VEVENT""><C:time-range start=""{T(from)}"" end=""{T(to)}""/></C:comp-filter></C:comp-filter></C:filter>
</C:calendar-query>";
            var req = Req(new HttpMethod("REPORT"), _url);
            req.Headers.Add("Depth", "1");
            req.Content = new StringContent(query, Encoding.UTF8, "application/xml");
            using var res = await _s.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException(L.F("CalDAV hatası: {0}", (int)res.StatusCode));
            var doc = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var list = new List<RemoteEvent>();
            foreach (var resp in doc.Descendants().Where(x => x.Name.LocalName == "response"))
            {
                var href = resp.Descendants().FirstOrDefault(x => x.Name.LocalName == "href")?.Value.Trim() ?? "";
                var etag = resp.Descendants().FirstOrDefault(x => x.Name.LocalName == "getetag")?.Value.Trim() ?? "";
                var data = resp.Descendants().FirstOrDefault(x => x.Name.LocalName == "calendar-data")?.Value.Trim() ?? "";
                if (href.Length == 0 || data.Length == 0) continue;
                // one resource = one event (+ its exceptions); the master is the one without RECURRENCE-ID
                var ev = Ics.Parse(data).Events.FirstOrDefault();
                if (ev == null) continue;
                list.Add(new RemoteEvent(href, etag, ev.Title, ev.Notes, ev.Location, ev.Start, ev.End, ev.AllDay) { Event = ev });
            }
            return (list, true);
        }

        public bool CanWrite(CalendarEvent e) => true;

        public async Task<(string?, string?)> CreateAsync(CalendarEvent e, CancellationToken ct)
        {
            var href = $"{_url}/{Uri.EscapeDataString(e.Uid)}.ics";
            var req = Req(HttpMethod.Put, href);
            req.Headers.TryAddWithoutValidation("If-None-Match", "*");
            req.Content = new StringContent(Ics.WriteCalendar(new[] { e }), Encoding.UTF8, "text/calendar");
            using var res = await _s.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return (null, null);
            return (new Uri(href).AbsolutePath, res.Headers.ETag?.Tag ?? "");
        }

        public async Task<string?> UpdateAsync(CalendarEvent e, CancellationToken ct)
        {
            var req = Req(HttpMethod.Put, Abs(e.ExternalId!));
            req.Content = new StringContent(Ics.WriteCalendar(new[] { e }), Encoding.UTF8, "text/calendar");
            using var res = await _s.SendAsync(req, ct);
            return res.IsSuccessStatusCode ? res.Headers.ETag?.Tag ?? "" : null;
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct)
        {
            using var res = await _s.SendAsync(Req(HttpMethod.Delete, Abs(id)), ct);
            return res.IsSuccessStatusCode || res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
        }
    }
}
