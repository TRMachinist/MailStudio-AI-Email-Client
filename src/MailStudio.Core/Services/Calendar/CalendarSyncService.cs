using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MailStudio.Models;

namespace MailStudio.Services.Calendar;

/// <summary>
/// Summary of a calendar synchronization run across one or more accounts.
/// </summary>
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
        if (!string.IsNullOrEmpty(other.Error))
            Error = string.IsNullOrEmpty(Error) ? other.Error : Error + "; " + other.Error;
    }
}

/// <summary>
/// Two-way synchronization service for Microsoft Graph (Outlook.com / Microsoft 365),
/// Google Calendar API v3, and CalDAV (Yandex, Nextcloud, custom).
///
/// Execution order:
/// 1. Read: pull changes from server to local CalendarStore (conflicts resolved: server wins with note).
/// 2. Write: push local additions, modifications and tombstones to server.
/// If sync is disabled on the account, zero network calls are performed.
/// </summary>
public class CalendarSyncService : IDisposable
{
    readonly CalendarStore _calendar;
    readonly OAuthService? _oauth;
    readonly HttpClient _http;
    readonly bool _ownsHttpClient;

    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public string GoogleBaseUrl { get; set; } = "https://www.googleapis.com/calendar/v3";
    public string? CustomAccessToken { get; set; }

    public CalendarSyncService(CalendarStore calendar, OAuthService? oauth = null, HttpClient? httpClient = null)
    {
        _calendar = calendar;
        _oauth = oauth;
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _http = new HttpClient();
            _ownsHttpClient = true;
        }
    }

    public CalendarSyncService(CalendarStore calendar, HttpMessageHandler handler)
        : this(calendar, null, new HttpClient(handler))
    {
        _ownsHttpClient = true;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    /// <summary>
    /// Synchronizes all enabled accounts sequentially.
    /// </summary>
    public async Task<CalendarSyncResult> SyncAllAccountsAsync(IEnumerable<AccountConfig> accounts, CancellationToken ct = default)
    {
        var total = new CalendarSyncResult();
        foreach (var acc in accounts)
        {
            if (!acc.EnableCalendarSync) continue;
            var r = await SyncAccountAsync(acc, ct);
            total.Merge(r);
        }
        return total;
    }

    /// <summary>
    /// Synchronizes a single account. If EnableCalendarSync is false, makes zero network calls.
    /// </summary>
    public async Task<CalendarSyncResult> SyncAccountAsync(AccountConfig account, CancellationToken ct = default)
    {
        if (!account.EnableCalendarSync)
        {
            return new CalendarSyncResult { Success = true, StatusMessage = L.T("Takvim eşitlemesi bu hesap için kapalı.") };
        }

        try
        {
            return account.AuthType switch
            {
                AuthType.Microsoft => await SyncMicrosoftGraphAsync(account, ct),
                AuthType.Google => await SyncGoogleCalendarAsync(account, ct),
                _ => await SyncCalDavAsync(account, ct)
            };
        }
        catch (Exception ex)
        {
            account.CalendarSyncStatus = L.T("Takvim eşitleme hatası: ") + ex.Message;
            return new CalendarSyncResult { Success = false, Error = ex.Message, StatusMessage = account.CalendarSyncStatus };
        }
    }

    // ------------------------------------------------------------------ Microsoft Graph

    async Task<CalendarSyncResult> SyncMicrosoftGraphAsync(AccountConfig account, CancellationToken ct)
    {
        var result = new CalendarSyncResult();
        string token = CustomAccessToken ?? "";
        if (string.IsNullOrEmpty(token))
        {
            if (_oauth == null)
                throw new InvalidOperationException(L.T("OAuth servisi bulunamadı."));
            try
            {
                token = await _oauth.GetCalendarAccessTokenAsync(account, ct);
            }
            catch (Exception ex)
            {
                account.CalendarSyncStatus = L.T("Takvim yetkilendirmesi gerekli. Lütfen hesap ayarlarından oturumu yenileyin.");
                return new CalendarSyncResult { Success = false, Error = ex.Message, StatusMessage = account.CalendarSyncStatus };
            }
        }

        var baseUrl = GraphBaseUrl.TrimEnd('/');

        // 1. READ PHASE (Sunucudan yerel takvime)
        var getReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/me/events?$top=250");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var getRes = await _http.SendAsync(getReq, ct);
        if (!getRes.IsSuccessStatusCode)
        {
            if (getRes.StatusCode == HttpStatusCode.Unauthorized || getRes.StatusCode == HttpStatusCode.Forbidden)
                account.CalendarSyncStatus = L.T("Takvim yetkilendirmesi gerekli. Lütfen hesap ayarlarından oturumu yenileyin.");
            else
                account.CalendarSyncStatus = L.F("Microsoft Graph hatası: {0}", (int)getRes.StatusCode);
            return new CalendarSyncResult { Success = false, Error = account.CalendarSyncStatus, StatusMessage = account.CalendarSyncStatus };
        }

        var getJson = await getRes.Content.ReadAsStringAsync(ct);
        var seenRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var doc = JsonDocument.Parse(getJson))
        {
            if (doc.RootElement.TryGetProperty("value", out var valueElem) && valueElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in valueElem.EnumerateArray())
                {
                    result.Fetched++;
                    var id = ev.GetProperty("id").GetString() ?? "";
                    if (string.IsNullOrEmpty(id)) continue;
                    seenRemoteIds.Add(id);

                    var changeKey = ev.TryGetProperty("changeKey", out var ck) ? ck.GetString() : null;
                    var subject = ev.TryGetProperty("subject", out var sb) ? sb.GetString() ?? "" : "";
                    var isAllDay = ev.TryGetProperty("isAllDay", out var ad) && ad.GetBoolean();
                    var location = ev.TryGetProperty("location", out var loc) && loc.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";

                    string notes = "";
                    if (ev.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var bc)) notes = bc.GetString() ?? "";
                    else if (ev.TryGetProperty("bodyPreview", out var bp)) notes = bp.GetString() ?? "";

                    DateTime start = DateTime.Now;
                    if (ev.TryGetProperty("start", out var s) && s.TryGetProperty("dateTime", out var sdt))
                        DateTime.TryParse(sdt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start);

                    DateTime end = start.AddHours(1);
                    if (ev.TryGetProperty("end", out var e) && e.TryGetProperty("dateTime", out var edt))
                        DateTime.TryParse(edt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out end);

                    var local = _calendar.FindByExternalId(id) ??
                                _calendar.Events.FirstOrDefault(x => x.AccountId == account.Id && x.ExternalId == id);

                    if (local == null)
                    {
                        // Check if deleted locally
                        var tomb = _calendar.GetTombstones(account.Id).FirstOrDefault(t => string.Equals(t.ExternalId, id, StringComparison.OrdinalIgnoreCase));
                        if (tomb != null) continue;

                        var newEv = new CalendarEvent
                        {
                            AccountId = account.Id,
                            ExternalId = id,
                            ExternalEtag = changeKey,
                            Title = subject,
                            Notes = notes,
                            Start = start,
                            End = end,
                            AllDay = isAllDay,
                            Location = location
                        };
                        _calendar.Save(newEv);
                        newEv.Modified = DateTime.Now.AddMinutes(-1);
                        newEv.LastSyncedUtc = DateTime.UtcNow;
                        result.CreatedLocally++;
                    }
                    else
                    {
                        bool localModified = local.LastSyncedUtc != null && local.Modified.ToUniversalTime() > local.LastSyncedUtc.Value;
                        bool remoteModified = local.ExternalEtag == null || !string.Equals(local.ExternalEtag, changeKey, StringComparison.Ordinal);

                        if (localModified && remoteModified)
                        {
                            // Conflict! Server wins with note
                            var conflictNote = L.F("[Çakışma çözüldü: Sunucu sürümü uygulandı. Yerel: {0} ({1:g})]", local.Title, local.Start);
                            local.Notes = string.IsNullOrWhiteSpace(notes) ? conflictNote : notes + "\n" + conflictNote;
                            local.Title = subject;
                            local.Start = start;
                            local.End = end;
                            local.Location = location;
                            local.AllDay = isAllDay;
                            local.ExternalEtag = changeKey;
                            _calendar.Save(local);
                            local.Modified = DateTime.Now.AddMinutes(-1);
                            local.LastSyncedUtc = DateTime.UtcNow;
                            result.ConflictsResolved++;
                            result.UpdatedLocally++;
                        }
                        else if (remoteModified)
                        {
                            local.Title = subject;
                            local.Notes = notes;
                            local.Start = start;
                            local.End = end;
                            local.Location = location;
                            local.AllDay = isAllDay;
                            local.ExternalEtag = changeKey;
                            _calendar.Save(local);
                            local.Modified = DateTime.Now.AddMinutes(-1);
                            local.LastSyncedUtc = DateTime.UtcNow;
                            result.UpdatedLocally++;
                        }
                    }
                }
            }
        }

        // Server deletions
        var localAccountEvents = _calendar.Events.Where(x => x.AccountId == account.Id && !string.IsNullOrEmpty(x.ExternalId)).ToList();
        foreach (var lev in localAccountEvents)
        {
            if (!seenRemoteIds.Contains(lev.ExternalId!))
            {
                _calendar.DeleteSilently(lev.Id);
                result.DeletedLocally++;
            }
        }

        // 2. WRITE PHASE (Yerelde yapılan değişiklikler sunucuya)
        // 2a. Process tombstones (deleted locally)
        var tombstones = _calendar.GetTombstones(account.Id);
        var removedTombstoneIds = new List<string>();
        foreach (var t in tombstones)
        {
            if (!string.IsNullOrEmpty(t.ExternalId))
            {
                var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/me/events/{t.ExternalId}");
                delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var delRes = await _http.SendAsync(delReq, ct);
                if (delRes.IsSuccessStatusCode || delRes.StatusCode == HttpStatusCode.NotFound)
                {
                    removedTombstoneIds.Add(t.Id);
                    result.DeletedRemote++;
                }
            }
            else
            {
                removedTombstoneIds.Add(t.Id);
            }
        }
        if (removedTombstoneIds.Count > 0) _calendar.RemoveTombstones(account.Id, removedTombstoneIds);

        // 2b. New local events (no ExternalId)
        var newLocals = _calendar.Events.Where(x => (x.AccountId == account.Id || string.IsNullOrEmpty(x.AccountId)) && string.IsNullOrEmpty(x.ExternalId)).ToList();
        foreach (var ev in newLocals)
        {
            ev.AccountId = account.Id;
            var payload = new
            {
                subject = ev.Title,
                body = new { contentType = "Text", content = ev.Notes ?? "" },
                start = new { dateTime = ev.Start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                end = new { dateTime = ev.End.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                location = new { displayName = ev.Location ?? "" },
                isAllDay = ev.AllDay
            };
            var postReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/me/events");
            postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            postReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var postRes = await _http.SendAsync(postReq, ct);
            if (postRes.IsSuccessStatusCode)
            {
                var pJson = await postRes.Content.ReadAsStringAsync(ct);
                using var pDoc = JsonDocument.Parse(pJson);
                ev.ExternalId = pDoc.RootElement.GetProperty("id").GetString();
                if (pDoc.RootElement.TryGetProperty("changeKey", out var ck)) ev.ExternalEtag = ck.GetString();
                _calendar.Save(ev);
                ev.Modified = DateTime.Now.AddMinutes(-1);
                ev.LastSyncedUtc = DateTime.UtcNow;
                result.CreatedRemote++;
            }
        }

        // 2c. Updated local events
        var modifiedLocals = _calendar.Events.Where(x => x.AccountId == account.Id && !string.IsNullOrEmpty(x.ExternalId)
                                                       && x.LastSyncedUtc != null && x.Modified.ToUniversalTime() > x.LastSyncedUtc.Value).ToList();
        foreach (var ev in modifiedLocals)
        {
            var payload = new
            {
                subject = ev.Title,
                body = new { contentType = "Text", content = ev.Notes ?? "" },
                start = new { dateTime = ev.Start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                end = new { dateTime = ev.End.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                location = new { displayName = ev.Location ?? "" },
                isAllDay = ev.AllDay
            };
            var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/me/events/{ev.ExternalId}");
            patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            patchReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var patchRes = await _http.SendAsync(patchReq, ct);
            if (patchRes.IsSuccessStatusCode)
            {
                var pJson = await patchRes.Content.ReadAsStringAsync(ct);
                using var pDoc = JsonDocument.Parse(pJson);
                if (pDoc.RootElement.TryGetProperty("changeKey", out var ck)) ev.ExternalEtag = ck.GetString();
                _calendar.Save(ev);
                ev.Modified = DateTime.Now.AddMinutes(-1);
                ev.LastSyncedUtc = DateTime.UtcNow;
                result.UpdatedRemote++;
            }
        }

        account.LastCalendarSyncUtc = DateTime.UtcNow;
        account.CalendarSyncStatus = L.T("Takvim eşitlemesi tamamlandı.");
        result.Success = true;
        result.StatusMessage = account.CalendarSyncStatus;
        return result;
    }

    // ------------------------------------------------------------------ Google Calendar

    async Task<CalendarSyncResult> SyncGoogleCalendarAsync(AccountConfig account, CancellationToken ct)
    {
        var result = new CalendarSyncResult();
        string token = CustomAccessToken ?? "";
        if (string.IsNullOrEmpty(token))
        {
            if (_oauth == null)
                throw new InvalidOperationException(L.T("OAuth servisi bulunamadı."));
            try
            {
                token = await _oauth.GetCalendarAccessTokenAsync(account, ct);
            }
            catch (Exception ex)
            {
                account.CalendarSyncStatus = L.T("Takvim yetkilendirmesi gerekli. Lütfen hesap ayarlarından oturumu yenileyin.");
                return new CalendarSyncResult { Success = false, Error = ex.Message, StatusMessage = account.CalendarSyncStatus };
            }
        }

        var baseUrl = GoogleBaseUrl.TrimEnd('/');

        // 1. READ PHASE
        var getReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/calendars/primary/events?maxResults=250");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var getRes = await _http.SendAsync(getReq, ct);
        if (!getRes.IsSuccessStatusCode)
        {
            if (getRes.StatusCode == HttpStatusCode.Unauthorized || getRes.StatusCode == HttpStatusCode.Forbidden)
                account.CalendarSyncStatus = L.T("Takvim yetkilendirmesi gerekli. Lütfen hesap ayarlarından oturumu yenileyin.");
            else
                account.CalendarSyncStatus = L.F("Google Calendar hatası: {0}", (int)getRes.StatusCode);
            return new CalendarSyncResult { Success = false, Error = account.CalendarSyncStatus, StatusMessage = account.CalendarSyncStatus };
        }

        var getJson = await getRes.Content.ReadAsStringAsync(ct);
        var seenRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var doc = JsonDocument.Parse(getJson))
        {
            if (doc.RootElement.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in itemsElem.EnumerateArray())
                {
                    result.Fetched++;
                    var id = ev.GetProperty("id").GetString() ?? "";
                    if (string.IsNullOrEmpty(id)) continue;

                    var status = ev.TryGetProperty("status", out var st) ? st.GetString() : "confirmed";
                    if (status == "cancelled")
                    {
                        // Treated as deleted on server
                        var delLocal = _calendar.FindByExternalId(id);
                        if (delLocal != null && delLocal.AccountId == account.Id)
                        {
                            _calendar.DeleteSilently(delLocal.Id);
                            result.DeletedLocally++;
                        }
                        continue;
                    }

                    seenRemoteIds.Add(id);
                    var etag = ev.TryGetProperty("etag", out var et) ? et.GetString() : null;
                    var summary = ev.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                    var description = ev.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
                    var location = ev.TryGetProperty("location", out var lc) ? lc.GetString() ?? "" : "";

                    bool isAllDay = false;
                    DateTime start = DateTime.Now;
                    DateTime end = start.AddHours(1);

                    if (ev.TryGetProperty("start", out var s))
                    {
                        if (s.TryGetProperty("date", out var sdate))
                        {
                            isAllDay = true;
                            DateTime.TryParse(sdate.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
                        }
                        else if (s.TryGetProperty("dateTime", out var sdt))
                        {
                            DateTime.TryParse(sdt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start);
                        }
                    }

                    if (ev.TryGetProperty("end", out var e))
                    {
                        if (e.TryGetProperty("date", out var edate))
                            DateTime.TryParse(edate.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out end);
                        else if (e.TryGetProperty("dateTime", out var edt))
                            DateTime.TryParse(edt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out end);
                    }

                    var local = _calendar.FindByExternalId(id) ??
                                _calendar.Events.FirstOrDefault(x => x.AccountId == account.Id && x.ExternalId == id);

                    if (local == null)
                    {
                        var tomb = _calendar.GetTombstones(account.Id).FirstOrDefault(t => string.Equals(t.ExternalId, id, StringComparison.OrdinalIgnoreCase));
                        if (tomb != null) continue;

                        var newEv = new CalendarEvent
                        {
                            AccountId = account.Id,
                            ExternalId = id,
                            ExternalEtag = etag,
                            Title = summary,
                            Notes = description,
                            Start = start,
                            End = end,
                            AllDay = isAllDay,
                            Location = location
                        };
                        _calendar.Save(newEv);
                        newEv.Modified = DateTime.Now.AddMinutes(-1);
                        newEv.LastSyncedUtc = DateTime.UtcNow;
                        result.CreatedLocally++;
                    }
                    else
                    {
                        bool localModified = local.LastSyncedUtc != null && local.Modified.ToUniversalTime() > local.LastSyncedUtc.Value;
                        bool remoteModified = local.ExternalEtag == null || !string.Equals(local.ExternalEtag, etag, StringComparison.Ordinal);

                        if (localModified && remoteModified)
                        {
                            var conflictNote = L.F("[Çakışma çözüldü: Sunucu sürümü uygulandı. Yerel: {0} ({1:g})]", local.Title, local.Start);
                            local.Notes = string.IsNullOrWhiteSpace(description) ? conflictNote : description + "\n" + conflictNote;
                            local.Title = summary;
                            local.Start = start;
                            local.End = end;
                            local.Location = location;
                            local.AllDay = isAllDay;
                            local.ExternalEtag = etag;
                            _calendar.Save(local);
                            local.Modified = DateTime.Now.AddMinutes(-1);
                            local.LastSyncedUtc = DateTime.UtcNow;
                            result.ConflictsResolved++;
                            result.UpdatedLocally++;
                        }
                        else if (remoteModified)
                        {
                            local.Title = summary;
                            local.Notes = description;
                            local.Start = start;
                            local.End = end;
                            local.Location = location;
                            local.AllDay = isAllDay;
                            local.ExternalEtag = etag;
                            _calendar.Save(local);
                            local.Modified = DateTime.Now.AddMinutes(-1);
                            local.LastSyncedUtc = DateTime.UtcNow;
                            result.UpdatedLocally++;
                        }
                    }
                }
            }
        }

        // Server deletions
        var localAccountEvents = _calendar.Events.Where(x => x.AccountId == account.Id && !string.IsNullOrEmpty(x.ExternalId)).ToList();
        foreach (var lev in localAccountEvents)
        {
            if (!seenRemoteIds.Contains(lev.ExternalId!))
            {
                _calendar.DeleteSilently(lev.Id);
                result.DeletedLocally++;
            }
        }

        // 2. WRITE PHASE
        // 2a. Tombstones
        var tombstones = _calendar.GetTombstones(account.Id);
        var removedTombstoneIds = new List<string>();
        foreach (var t in tombstones)
        {
            if (!string.IsNullOrEmpty(t.ExternalId))
            {
                var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/calendars/primary/events/{t.ExternalId}");
                delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var delRes = await _http.SendAsync(delReq, ct);
                if (delRes.IsSuccessStatusCode || delRes.StatusCode == HttpStatusCode.NotFound)
                {
                    removedTombstoneIds.Add(t.Id);
                    result.DeletedRemote++;
                }
            }
            else
            {
                removedTombstoneIds.Add(t.Id);
            }
        }
        if (removedTombstoneIds.Count > 0) _calendar.RemoveTombstones(account.Id, removedTombstoneIds);

        // 2b. New local events
        var newLocals = _calendar.Events.Where(x => (x.AccountId == account.Id || string.IsNullOrEmpty(x.AccountId)) && string.IsNullOrEmpty(x.ExternalId)).ToList();
        foreach (var ev in newLocals)
        {
            ev.AccountId = account.Id;
            object startObj = ev.AllDay
                ? new { date = ev.Start.ToString("yyyy-MM-dd") }
                : new { dateTime = ev.Start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") };
            object endObj = ev.AllDay
                ? new { date = ev.End.ToString("yyyy-MM-dd") }
                : new { dateTime = ev.End.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") };

            var payload = new
            {
                summary = ev.Title,
                description = ev.Notes ?? "",
                location = ev.Location ?? "",
                start = startObj,
                end = endObj
            };
            var postReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/calendars/primary/events");
            postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            postReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var postRes = await _http.SendAsync(postReq, ct);
            if (postRes.IsSuccessStatusCode)
            {
                var pJson = await postRes.Content.ReadAsStringAsync(ct);
                using var pDoc = JsonDocument.Parse(pJson);
                ev.ExternalId = pDoc.RootElement.GetProperty("id").GetString();
                if (pDoc.RootElement.TryGetProperty("etag", out var et)) ev.ExternalEtag = et.GetString();
                _calendar.Save(ev);
                ev.Modified = DateTime.Now.AddMinutes(-1);
                ev.LastSyncedUtc = DateTime.UtcNow;
                result.CreatedRemote++;
            }
        }

        // 2c. Updated local events
        var modifiedLocals = _calendar.Events.Where(x => x.AccountId == account.Id && !string.IsNullOrEmpty(x.ExternalId)
                                                       && x.LastSyncedUtc != null && x.Modified.ToUniversalTime() > x.LastSyncedUtc.Value).ToList();
        foreach (var ev in modifiedLocals)
        {
            object startObj = ev.AllDay
                ? new { date = ev.Start.ToString("yyyy-MM-dd") }
                : new { dateTime = ev.Start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") };
            object endObj = ev.AllDay
                ? new { date = ev.End.ToString("yyyy-MM-dd") }
                : new { dateTime = ev.End.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") };

            var payload = new
            {
                summary = ev.Title,
                description = ev.Notes ?? "",
                location = ev.Location ?? "",
                start = startObj,
                end = endObj
            };
            var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/calendars/primary/events/{ev.ExternalId}");
            patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            patchReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var patchRes = await _http.SendAsync(patchReq, ct);
            if (patchRes.IsSuccessStatusCode)
            {
                var pJson = await patchRes.Content.ReadAsStringAsync(ct);
                using var pDoc = JsonDocument.Parse(pJson);
                if (pDoc.RootElement.TryGetProperty("etag", out var et)) ev.ExternalEtag = et.GetString();
                _calendar.Save(ev);
                ev.Modified = DateTime.Now.AddMinutes(-1);
                ev.LastSyncedUtc = DateTime.UtcNow;
                result.UpdatedRemote++;
            }
        }

        account.LastCalendarSyncUtc = DateTime.UtcNow;
        account.CalendarSyncStatus = L.T("Takvim eşitlemesi tamamlandı.");
        result.Success = true;
        result.StatusMessage = account.CalendarSyncStatus;
        return result;
    }

    // ------------------------------------------------------------------ CalDAV (RFC 4791)

    string GetCalDavUrl(AccountConfig account)
    {
        if (!string.IsNullOrWhiteSpace(account.CalDavUrl))
            return account.CalDavUrl.TrimEnd('/');

        var user = !string.IsNullOrWhiteSpace(account.CalDavUser)
            ? account.CalDavUser
            : (!string.IsNullOrWhiteSpace(account.UserName) ? account.UserName : account.Email);

        if (account.ImapHost.Contains("yandex", StringComparison.OrdinalIgnoreCase))
            return $"https://caldav.yandex.ru/calendars/{user}/events";

        var host = string.IsNullOrWhiteSpace(account.ImapHost) ? "localhost" : account.ImapHost;
        return $"https://{host}/caldav/calendars/{user}/events";
    }

    AuthenticationHeaderValue GetCalDavAuthHeader(AccountConfig account)
    {
        if (!string.IsNullOrEmpty(CustomAccessToken))
            return new AuthenticationHeaderValue("Bearer", CustomAccessToken);

        var user = !string.IsNullOrWhiteSpace(account.CalDavUser)
            ? account.CalDavUser
            : (!string.IsNullOrWhiteSpace(account.UserName) ? account.UserName : account.Email);

        var pass = !string.IsNullOrWhiteSpace(account.CalDavPassword)
            ? account.CalDavPassword
            : AccountStore.Unprotect(account.ProtectedPassword);

        var bytes = Encoding.UTF8.GetBytes($"{user}:{pass}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
    }

    async Task<CalendarSyncResult> SyncCalDavAsync(AccountConfig account, CancellationToken ct)
    {
        var result = new CalendarSyncResult();
        var calDavUrl = GetCalDavUrl(account);
        var authHeader = GetCalDavAuthHeader(account);

        // 1. READ PHASE (REPORT or GET)
        var queryXml = @"<C:calendar-query xmlns:D=""DAV:"" xmlns:C=""urn:ietf:params:xml:ns:caldav"">
  <D:prop>
    <D:getetag/>
    <C:calendar-data/>
  </D:prop>
  <C:filter>
    <C:comp-filter name=""VCALENDAR"">
      <C:comp-filter name=""VEVENT""/>
    </C:comp-filter>
  </C:filter>
</C:calendar-query>";

        var reportReq = new HttpRequestMessage(new HttpMethod("REPORT"), calDavUrl);
        reportReq.Headers.Authorization = authHeader;
        reportReq.Headers.Add("Depth", "1");
        reportReq.Content = new StringContent(queryXml, Encoding.UTF8, "application/xml");

        var reportRes = await _http.SendAsync(reportReq, ct);
        if (!reportRes.IsSuccessStatusCode)
        {
            if (reportRes.StatusCode == HttpStatusCode.Unauthorized || reportRes.StatusCode == HttpStatusCode.Forbidden)
                account.CalendarSyncStatus = L.T("Takvim yetkilendirmesi gerekli. Lütfen hesap ayarlarından oturumu yenileyin.");
            else
                account.CalendarSyncStatus = L.F("CalDAV hatası: {0}", (int)reportRes.StatusCode);
            return new CalendarSyncResult { Success = false, Error = account.CalendarSyncStatus, StatusMessage = account.CalendarSyncStatus };
        }

        var xmlContent = await reportRes.Content.ReadAsStringAsync(ct);
        var seenRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var doc = XDocument.Parse(xmlContent);
            foreach (var resp in doc.Descendants().Where(x => x.Name.LocalName == "response"))
            {
                var href = resp.Descendants().FirstOrDefault(x => x.Name.LocalName == "href")?.Value?.Trim() ?? "";
                var etag = resp.Descendants().FirstOrDefault(x => x.Name.LocalName == "getetag")?.Value?.Trim() ?? "";
                var calData = resp.Descendants().FirstOrDefault(x => x.Name.LocalName == "calendar-data")?.Value?.Trim() ?? "";

                if (string.IsNullOrEmpty(calData)) continue;
                result.Fetched++;

                var parsed = Ics.Parse(calData);
                foreach (var ev in parsed.Events)
                {
                    var extId = !string.IsNullOrEmpty(href) ? href : ev.Uid;
                    seenRemoteIds.Add(extId);

                    var local = _calendar.FindByExternalId(extId) ??
                                _calendar.Events.FirstOrDefault(x => x.AccountId == account.Id && (x.ExternalId == extId || x.Uid == ev.Uid));

                    if (local == null)
                    {
                        var tomb = _calendar.GetTombstones(account.Id).FirstOrDefault(t => string.Equals(t.ExternalId, extId, StringComparison.OrdinalIgnoreCase));
                        if (tomb != null) continue;

                        ev.AccountId = account.Id;
                        ev.ExternalId = extId;
                        ev.ExternalEtag = etag;
                        _calendar.Save(ev);
                        ev.Modified = DateTime.Now.AddMinutes(-1);
                        ev.LastSyncedUtc = DateTime.UtcNow;
                        result.CreatedLocally++;
                    }
                    else
                    {
                        bool localModified = local.LastSyncedUtc != null && local.Modified.ToUniversalTime() > local.LastSyncedUtc.Value;
                        bool remoteModified = local.ExternalEtag == null || !string.Equals(local.ExternalEtag, etag, StringComparison.Ordinal);

                        if (localModified && remoteModified)
                        {
                            var conflictNote = L.F("[Çakışma çözüldü: Sunucu sürümü uygulandı. Yerel: {0} ({1:g})]", local.Title, local.Start);
                            local.Notes = string.IsNullOrWhiteSpace(ev.Notes) ? conflictNote : ev.Notes + "\n" + conflictNote;
                            local.Title = ev.Title;
                            local.Start = ev.Start;
                            local.End = ev.End;
                            local.Location = ev.Location;
                            local.AllDay = ev.AllDay;
                            local.ExternalEtag = etag;
                            _calendar.Save(local);
                            local.Modified = DateTime.Now.AddMinutes(-1);
                            local.LastSyncedUtc = DateTime.UtcNow;
                            result.ConflictsResolved++;
                            result.UpdatedLocally++;
                        }
                        else if (remoteModified)
                        {
                            local.Title = ev.Title;
                            local.Notes = ev.Notes;
                            local.Start = ev.Start;
                            local.End = ev.End;
                            local.Location = ev.Location;
                            local.AllDay = ev.AllDay;
                            local.ExternalEtag = etag;
                            _calendar.Save(local);
                            local.Modified = DateTime.Now.AddMinutes(-1);
                            local.LastSyncedUtc = DateTime.UtcNow;
                            result.UpdatedLocally++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            account.CalendarSyncStatus = L.T("CalDAV XML ayrıştırma hatası: ") + ex.Message;
            return new CalendarSyncResult { Success = false, Error = ex.Message, StatusMessage = account.CalendarSyncStatus };
        }

        // Server deletions
        var localAccountEvents = _calendar.Events.Where(x => x.AccountId == account.Id && !string.IsNullOrEmpty(x.ExternalId)).ToList();
        foreach (var lev in localAccountEvents)
        {
            if (!seenRemoteIds.Contains(lev.ExternalId!))
            {
                _calendar.DeleteSilently(lev.Id);
                result.DeletedLocally++;
            }
        }

        // 2. WRITE PHASE
        // 2a. Tombstones
        var tombstones = _calendar.GetTombstones(account.Id);
        var removedTombstoneIds = new List<string>();
        foreach (var t in tombstones)
        {
            if (!string.IsNullOrEmpty(t.ExternalId))
            {
                var delUrl = t.ExternalId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? t.ExternalId
                    : $"{calDavUrl}/{t.ExternalId.TrimStart('/')}";
                var delReq = new HttpRequestMessage(HttpMethod.Delete, delUrl);
                delReq.Headers.Authorization = authHeader;
                var delRes = await _http.SendAsync(delReq, ct);
                if (delRes.IsSuccessStatusCode || delRes.StatusCode == HttpStatusCode.NotFound)
                {
                    removedTombstoneIds.Add(t.Id);
                    result.DeletedRemote++;
                }
            }
            else
            {
                removedTombstoneIds.Add(t.Id);
            }
        }
        if (removedTombstoneIds.Count > 0) _calendar.RemoveTombstones(account.Id, removedTombstoneIds);

        // 2b. New local events
        var newLocals = _calendar.Events.Where(x => (x.AccountId == account.Id || string.IsNullOrEmpty(x.AccountId)) && string.IsNullOrEmpty(x.ExternalId)).ToList();
        foreach (var ev in newLocals)
        {
            ev.AccountId = account.Id;
            var putUrl = $"{calDavUrl}/{ev.Uid}.ics";
            var putReq = new HttpRequestMessage(HttpMethod.Put, putUrl);
            putReq.Headers.Authorization = authHeader;
            putReq.Content = new StringContent(Ics.WriteCalendar(new[] { ev }), Encoding.UTF8, "text/calendar");
            var putRes = await _http.SendAsync(putReq, ct);
            if (putRes.IsSuccessStatusCode)
            {
                ev.ExternalId = putUrl;
                ev.ExternalEtag = putRes.Headers.ETag?.Tag ?? "";
                _calendar.Save(ev);
                ev.Modified = DateTime.Now.AddMinutes(-1);
                ev.LastSyncedUtc = DateTime.UtcNow;
                result.CreatedRemote++;
            }
        }

        // 2c. Updated local events
        var modifiedLocals = _calendar.Events.Where(x => x.AccountId == account.Id && !string.IsNullOrEmpty(x.ExternalId)
                                                       && x.LastSyncedUtc != null && x.Modified.ToUniversalTime() > x.LastSyncedUtc.Value).ToList();
        foreach (var ev in modifiedLocals)
        {
            var putUrl = ev.ExternalId!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? ev.ExternalId!
                : $"{calDavUrl}/{ev.ExternalId!.TrimStart('/')}";
            var putReq = new HttpRequestMessage(HttpMethod.Put, putUrl);
            putReq.Headers.Authorization = authHeader;
            putReq.Content = new StringContent(Ics.WriteCalendar(new[] { ev }), Encoding.UTF8, "text/calendar");
            var putRes = await _http.SendAsync(putReq, ct);
            if (putRes.IsSuccessStatusCode)
            {
                ev.ExternalEtag = putRes.Headers.ETag?.Tag ?? ev.ExternalEtag;
                _calendar.Save(ev);
                ev.Modified = DateTime.Now.AddMinutes(-1);
                ev.LastSyncedUtc = DateTime.UtcNow;
                result.UpdatedRemote++;
            }
        }

        account.LastCalendarSyncUtc = DateTime.UtcNow;
        account.CalendarSyncStatus = L.T("Takvim eşitlemesi tamamlandı.");
        result.Success = true;
        result.StatusMessage = account.CalendarSyncStatus;
        return result;
    }
}
