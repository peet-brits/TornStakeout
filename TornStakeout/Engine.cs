using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TornStakeout;

// ---------------------------------------------------------------- config

public class AppConfig
{
    public string ApiKey { get; set; } = "";
    public string LinkMode { get; set; } = "hyperlink";
    public int CallsPerTick { get; set; } = 5;
    public int TickIntervalSeconds { get; set; } = 2;
    public int MaxCallsPerMinute { get; set; } = 80;
    public int OkayRecheckSeconds { get; set; } = 7;
    public int BusyRecheckSeconds { get; set; } = 120;
    public int ReleaseRecheckDelaySeconds { get; set; } = 10;
    public int AwayRecheckSeconds { get; set; } = 240;
    public int ErrorRetrySeconds { get; set; } = 300;

    // Last action colours. A number plus a unit: s (seconds), m (minutes), h (hours) or d (days), e.g. "15m", "7d".
    // Within the red limit = red, otherwise within the yellow limit = yellow, otherwise no colour.
    // Blank (the default) or anything that can't be read turns that colour off.
    public string LastActionRedWithin { get; set; } = "15m";
    public string LastActionYellowWithin { get; set; } = "7d";

    [JsonIgnore] public long LastActionRedSeconds { get; private set; } = -1;     // the two settings above in seconds; -1 = off
    [JsonIgnore] public long LastActionYellowSeconds { get; private set; } = -1;

    public int ActiveBusyRecheckSeconds { get; set; } = 0; //20;
    public int ActiveWithinMinutes { get; set; } = 0; //10;

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // "15m" -> 900, "7d" -> 604800. Blank or anything unreadable -> -1, which switches that colour off.
    static long ParseDuration(string? text)
    {
        var m = Regex.Match(text ?? "", @"^\s*(\d{1,6})\s*([smhd])\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return -1;
        var n = long.Parse(m.Groups[1].Value);
        return char.ToLowerInvariant(m.Groups[2].Value[0]) switch
        {
            's' => n,
            'm' => n * 60,
            'h' => n * 3600,
            _ => n * 86400
        };
    }

    // Settings come from config.json; local.json (same folder) overrides any setting it contains,
    // so the API key can live in local.json, which you keep out of git.
    // The TORN_API_KEY environment variable, if set, overrides the key from both files.
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, JsonSerializer.Serialize(new AppConfig(), Opts));

        var settings = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();

        var localPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "local.json");
        if (File.Exists(localPath))
        {
            var local = JsonNode.Parse(File.ReadAllText(localPath))?.AsObject();
            if (local != null)
            {
                foreach (var kv in local.ToList())
                {
                    local.Remove(kv.Key);          // detach the value from local before moving it
                    settings[kv.Key] = kv.Value;
                }
            }
        }

        var cfg = settings.Deserialize<AppConfig>() ?? new AppConfig();

        var envKey = Environment.GetEnvironmentVariable("TORN_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) cfg.ApiKey = envKey.Trim();

        cfg.CallsPerTick = Math.Max(1, cfg.CallsPerTick);
        cfg.TickIntervalSeconds = Math.Max(1, cfg.TickIntervalSeconds);
        cfg.MaxCallsPerMinute = Math.Max(1, cfg.MaxCallsPerMinute);
        cfg.ActiveWithinMinutes = Math.Max(0, cfg.ActiveWithinMinutes);
        cfg.ReleaseRecheckDelaySeconds = Math.Max(0, cfg.ReleaseRecheckDelaySeconds);

        cfg.LastActionRedSeconds = ParseDuration(cfg.LastActionRedWithin);
        cfg.LastActionYellowSeconds = ParseDuration(cfg.LastActionYellowWithin);
        return cfg;
    }
}

// ---------------------------------------------------------------- data model (this is what gets saved to data.json)

public class UserRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    public int? LifeCurrent { get; set; }
    public int? LifeMaximum { get; set; }

    public string? StatusDescription { get; set; }
    public string? StatusDetails { get; set; }
    public string? StatusState { get; set; }
    public string? StatusColor { get; set; }
    public long Until { get; set; }                 // 0 when okay / abroad / travelling

    public long HospitalTimestamp { get; set; }
    public long JailTimestamp { get; set; }

    public string? LastActionStatus { get; set; }
    public long LastActionTimestamp { get; set; }
    public string? LastActionRelative { get; set; }

    public long Bounty { get; set; }                // dollar amount; 0 = no bounty, -1 = bounty shown but amount unreadable

    public DateTimeOffset? LastUpdate { get; set; }   // last successful update
    public DateTimeOffset? LastAttempt { get; set; }  // last time we tried
    public string? Error { get; set; }                // API error from the last answer, null if it was fine
}

public class DataFile
{
    public DateTimeOffset? LastSaved { get; set; }
    public List<UserRecord> Users { get; set; } = new();
}

// ---------------------------------------------------------------- what the screen shows

public enum Section { Main, Attention }

public class Row
{
    public long Id;
    public string Name = "";
    public Section Section;
    public bool HasData;
    public bool IsError;
    public long Until;
    public string Location = "";   // Okay / Hospital / Jail ...
    public string Secs = "";       // remaining seconds
    public string Reason = "";     // attention section: why the user is listed
    public string Life = "";
    public string LastAction = "";
    public string Color = "";      // status.color from the API
    public long AgeSeconds = -1;   // seconds since the last action; -1 = nothing to colour (bounty shown, or unknown)
}

// ---------------------------------------------------------------- json helpers

static class J
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static long Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}

// ---------------------------------------------------------------- the "database"

public class DataStore
{
    readonly string _path;
    readonly object _lock = new();
    List<UserRecord> _users = new();
    DateTimeOffset? _lastSaved;
    bool _dirty;

    public DataStore(string path) => _path = path;

    public DateTimeOffset? LastSaved { get { lock (_lock) return _lastSaved; } }
    public int Count { get { lock (_lock) return _users.Count; } }

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var d = JsonSerializer.Deserialize<DataFile>(File.ReadAllText(_path));
            if (d != null) { _users = d.Users; _lastSaved = d.LastSaved; }
        }
        catch
        {
            File.Copy(_path, _path + ".bad", true);   // keep the unreadable file, start fresh
        }
    }

    // users.txt accepts "Sample [1234]" or just "1234"; '#' starts a comment line.
    // users.txt decides who is tracked; saved data for users still in the list is kept.
    public int MergeUserList(string listPath, out int skipped)
    {
        skipped = 0;
        var found = new List<(long id, string name)>();
        foreach (var line in File.ReadAllLines(listPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var m = Regex.Match(line, @"^\s*(?:(?<name>.*?)\s*\[(?<id>\d+)\]|(?<id2>\d+))\s*$");
            if (!m.Success) { skipped++; continue; }
            var idText = m.Groups["id"].Success ? m.Groups["id"].Value : m.Groups["id2"].Value;
            if (!long.TryParse(idText, out var id)) { skipped++; continue; }
            found.Add((id, m.Groups["name"].Success ? m.Groups["name"].Value : ""));
        }

        lock (_lock)
        {
            var existing = new Dictionary<long, UserRecord>();
            foreach (var u in _users) existing[u.Id] = u;

            var result = new List<UserRecord>();
            var seen = new HashSet<long>();
            foreach (var (id, name) in found)
            {
                if (!seen.Add(id)) continue;
                if (!existing.TryGetValue(id, out var rec)) rec = new UserRecord { Id = id, Name = name };
                else if (string.IsNullOrEmpty(rec.Name)) rec.Name = name;
                result.Add(rec);
            }
            _users = result;
            Save();
            return _users.Count;
        }
    }

    // Called once at startup (not on reload): saved "okay" players may have changed while the app was closed,
    // so they go back to "waiting..." and are queried first, exactly as if their entry were removed from data.json.
    // Players in hospital/jail, abroad, travelling or with an error keep their saved data.
    public int ResetOkayPlayers()
    {
        lock (_lock)
        {
            var count = 0;
            for (var i = 0; i < _users.Count; i++)
            {
                var u = _users[i];
                if (u.LastUpdate != null && u.Error == null && u.Until == 0 &&
                    u.StatusState is not ("Abroad" or "Traveling"))
                {
                    _users[i] = new UserRecord { Id = u.Id, Name = u.Name };   // keep only id and name
                    count++;
                }
            }
            if (count > 0) _dirty = true;
            return count;
        }
    }

    static DateTime NextDue(UserRecord u, AppConfig c)
    {
        if (u.LastAttempt is null) return DateTime.MinValue;               // never queried: due now
        var attempt = u.LastAttempt.Value.UtcDateTime;
        if (u.Error != null) return attempt.AddSeconds(c.ErrorRetrySeconds);
        if (u.StatusState is "Abroad" or "Traveling") return attempt.AddSeconds(c.AwayRecheckSeconds);
        if (u.Until == 0) return attempt.AddSeconds(c.OkayRecheckSeconds);
        var until = DateTimeOffset.FromUnixTimeSeconds(u.Until).UtcDateTime;   // when the timer is expected to end
        // players who acted recently can leave hospital/jail at any moment, so they get the faster recheck
        var active = c.ActiveWithinMinutes > 0 && c.ActiveBusyRecheckSeconds > 0 && u.LastActionTimestamp > 0 &&
                     DateTimeOffset.UtcNow.ToUnixTimeSeconds() - u.LastActionTimestamp <= c.ActiveWithinMinutes * 60L;
        var safety = attempt.AddSeconds(active ? c.ActiveBusyRecheckSeconds : c.BusyRecheckSeconds);
        // The safety re-check is only used if it lands while the player is still expected to be in hospital/jail.
        // Otherwise the only re-check is at release time + ReleaseRecheckDelaySeconds; the two never compete.
        return safety < until ? safety : until.AddSeconds(c.ReleaseRecheckDelaySeconds);
    }

    // Picks up to 'max' most-overdue users and marks the attempt.
    public List<long> PickDue(AppConfig cfg, int max)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var due = _users
                .Select(u => (u, at: NextDue(u, cfg)))
                .Where(x => x.at <= now)
                .OrderBy(x => x.at)
                .Take(max)
                .Select(x => x.u)
                .ToList();
            foreach (var u in due) u.LastAttempt = DateTimeOffset.UtcNow;
            return due.Select(u => u.Id).ToList();
        }
    }

    public void ApplySuccess(long id, JsonElement root)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => x.Id == id);
            if (u == null) return;

            var name = J.Str(root, "name");
            if (!string.IsNullOrEmpty(name)) u.Name = name;

            var life = J.Obj(root, "life");
            u.LifeCurrent = (int)J.Num(life, "current");
            u.LifeMaximum = (int)J.Num(life, "maximum");

            var st = J.Obj(root, "status");
            u.StatusDescription = J.Str(st, "description");
            u.StatusDetails = J.Str(st, "details");
            u.StatusState = J.Str(st, "state");
            u.StatusColor = J.Str(st, "color");
            u.Until = J.Num(st, "until");

            var states = J.Obj(root, "states");
            u.HospitalTimestamp = J.Num(states, "hospital_timestamp");
            u.JailTimestamp = J.Num(states, "jail_timestamp");

            var la = J.Obj(root, "last_action");
            u.LastActionStatus = J.Str(la, "status");
            u.LastActionTimestamp = J.Num(la, "timestamp");
            u.LastActionRelative = J.Str(la, "relative");

            // basicicons is an object of icons, or an empty array when there are none; J.Str copes with both
            u.Bounty = ParseBounty(J.Str(J.Obj(root, "basicicons"), "icon13"));

            u.LastUpdate = DateTimeOffset.UtcNow;
            u.Error = null;
            _dirty = true;
        }
    }

    public void ApplyError(long id, string message)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => x.Id == id);
            if (u == null) return;
            u.Error = message;
            _dirty = true;
        }
    }

    public void SaveIfDirty()
    {
        lock (_lock)
        {
            if (_dirty) Save();
        }
    }

    void Save()   // call while holding _lock
    {
        _dirty = false;
        _lastSaved = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(new DataFile { LastSaved = _lastSaved, Users = _users },
            new JsonSerializerOptions { WriteIndented = true });
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, true);
    }

    // Main list: sorted by status.until (lowest first). Attention list: errors first, then by name.
    public (List<Row> main, List<Row> attention) Snapshot()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var rows = _users.Select(u => ToRow(u, now)).ToList();

            var main = rows.Where(r => r.Section == Section.Main)
                .OrderBy(r => r.HasData ? 0 : 1)
                .ThenBy(r => r.Until)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var attention = rows.Where(r => r.Section == Section.Attention)
                .OrderBy(r => r.IsError ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (main, attention);
        }
    }

    static Row ToRow(UserRecord u, DateTimeOffset now)
    {
        var r = new Row
        {
            Id = u.Id,
            Name = string.IsNullOrEmpty(u.Name) ? $"[{u.Id}]" : u.Name,
        };

        if (u.Error != null)
        {
            r.Section = Section.Attention;
            r.IsError = true;
            r.Reason = u.Error;
            r.LastAction = LastActionText(u, now);
            r.AgeSeconds = ActionAge(u, now);
            return r;
        }

        if (u.LastUpdate == null)
        {
            r.Section = Section.Main;
            r.Location = "waiting...";
            return r;
        }

        r.HasData = true;
        r.Life = $"{u.LifeCurrent}/{u.LifeMaximum}";
        r.LastAction = LastActionText(u, now);
        r.AgeSeconds = ActionAge(u, now);
        r.Color = u.StatusColor ?? "";

        switch (u.StatusState)
        {
            case "Abroad":
                r.Section = Section.Attention;
                r.Reason = string.IsNullOrWhiteSpace(u.StatusDescription) ? "Abroad" : u.StatusDescription!;
                break;
            case "Traveling":
                r.Section = Section.Attention;
                r.Reason = u.StatusDescription ?? "Travelling"; //(u.StatusDescription ?? "").Contains("Torn") ? "Travelling to Torn" : "Travelling";
                break;
            default:
                r.Section = Section.Main;
                r.Location = string.IsNullOrEmpty(u.StatusState) ? "?" : u.StatusState!;
                r.Until = u.Until;
                if (u.Until > 0)
                    r.Secs = FormatRemaining(Math.Max(0, u.Until - now.ToUnixTimeSeconds()));
                break;
        }
        return r;
    }

    // 45 -> "45s", 323 -> "5m 23s", 9000 -> "2h 30m", 200000 -> "2d 7h"
    static string FormatRemaining(long totalSeconds)
    {
        var t = TimeSpan.FromSeconds(totalSeconds);
        if (t.TotalMinutes < 1) return $"{t.Seconds}s";
        if (t.TotalHours < 1) return $"{t.Minutes}m {t.Seconds}s";
        if (t.TotalDays < 1) return $"{t.Hours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    // "Bounty - On this person's head for $200,000" -> 200000; -1 if there is a bounty but no readable amount
    static long ParseBounty(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return 0;
        var m = Regex.Match(icon, @"\$\s*([\d,]+)");
        return m.Success && long.TryParse(m.Groups[1].Value.Replace(",", ""), out var v) && v > 0 ? v : -1;
    }

    // 950 -> "$950", 200000 -> "$200k", 1500000 -> "$1.5m", 2000000000 -> "$2b"
    static string CompactMoney(long value)
    {
        string[] units = { "", "k", "m", "b" };
        double d = value;
        var unit = 0;
        while (d >= 1000 && unit < 3) { d /= 1000; unit++; }
        d = Math.Round(d, 2);
        if (d >= 1000 && unit < 3) { d /= 1000; unit++; }      // e.g. 999,999 rounds up to 1000k -> 1m
        return "$" + d.ToString("0.##", CultureInfo.InvariantCulture) + units[unit];
    }

    // Seconds since the last action, rounded down to the unit shown on screen (so "2d ago" counts as exactly 2 days
    // and the colours match what you read). -1 when there is nothing to colour (a bounty is shown, or no timestamp).
    static long ActionAge(UserRecord u, DateTimeOffset now)
    {
        if (u.Bounty != 0 || u.LastActionTimestamp <= 0) return -1;
        var secs = Math.Max(0, now.ToUnixTimeSeconds() - u.LastActionTimestamp);
        var unit = secs < 60 ? 1 : secs < 3600 ? 60 : secs < 86400 ? 3600 : 86400;   // same cut-offs as Ago()
        return secs / unit * unit;
    }

    static string LastActionText(UserRecord u, DateTimeOffset now)
    {
        if (u.Bounty != 0)                                      // a bounty replaces the last action
            return u.Bounty > 0 ? "[B] " + CompactMoney(u.Bounty) : "[B]";
        if (u.LastActionTimestamp > 0)
            return Ago(now - DateTimeOffset.FromUnixTimeSeconds(u.LastActionTimestamp));
        return u.LastActionRelative ?? "";
    }

    static string Ago(TimeSpan t)
    {
        if (t.TotalSeconds < 0) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s ago";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m ago";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h ago";
        return $"{(int)t.TotalDays}d ago";
    }
}

// ---------------------------------------------------------------- the paced API loop

public class Poller
{
    readonly AppConfig _cfg;
    readonly DataStore _store;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    long _calls;

    public volatile string Message = "Starting...";
    public volatile string LimitNote = "";          // stays on screen once the rate limit has been hit
    public long TotalCalls => Interlocked.Read(ref _calls);

    // call statistics, used to see how many calls fit before "too many requests"
    readonly object _statLock = new();
    readonly Queue<DateTime> _callTimes = new();    // calls in the last 60 s
    DateTime _pausedUntil = DateTime.MinValue;
    int _limitHits;

    void RecordCall()
    {
        lock (_statLock)
        {
            var now = DateTime.UtcNow;
            _callTimes.Enqueue(now);
            TrimCalls(now);
        }
    }

    void TrimCalls(DateTime now)   // call while holding _statLock
    {
        while (_callTimes.Count > 0 && (now - _callTimes.Peek()).TotalSeconds > 60) _callTimes.Dequeue();
    }

    public int CallsLastMinute
    {
        get { lock (_statLock) { TrimCalls(DateTime.UtcNow); return _callTimes.Count; } }
    }

    public int PauseSecondsLeft
    {
        get { lock (_statLock) return Math.Max(0, (int)Math.Ceiling((_pausedUntil - DateTime.UtcNow).TotalSeconds)); }
    }

    void RegisterLimitHit(int pauseMs)
    {
        lock (_statLock)
        {
            var now = DateTime.UtcNow;
            TrimCalls(now);
            var lastMinute = _callTimes.Count;
            var lastSecond = _callTimes.Count(t => (now - t).TotalSeconds <= 1);
            _limitHits++;
            _pausedUntil = now.AddMilliseconds(pauseMs);
            LimitNote = $"Rate limit hit at {DateTime.Now:HH:mm:ss} (hit #{_limitHits}): " +
                        $"{lastMinute} calls in the last 60 s, {lastSecond} in the last second";
        }
    }

    public Poller(AppConfig cfg, DataStore store) { _cfg = cfg; _store = store; }

    // Every tick: take up to CallsPerTick users that are due and query them at the same time.
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                var extraDelayMs = 0;

                // never more than CallsPerTick at once, and never more than MaxCallsPerMinute in any 60 s
                var allowed = Math.Min(_cfg.CallsPerTick, _cfg.MaxCallsPerMinute - CallsLastMinute);
                var ids = allowed > 0 ? _store.PickDue(_cfg, allowed) : new List<long>();
                if (allowed <= 0)
                    Message = $"{DateTime.Now:HH:mm:ss}  Call budget used up ({_cfg.MaxCallsPerMinute}/min), waiting";
                if (ids.Count > 0)
                {
                    var results = await Task.WhenAll(ids.Select(id => QueryAsync(id, ct)));
                    extraDelayMs = results.Max();
                    _store.SaveIfDirty();
                }

                var wait = Math.Max(0, _cfg.TickIntervalSeconds * 1000 - (int)sw.ElapsedMilliseconds) + extraDelayMs;
                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // returns extra milliseconds to pause before the next tick
    async Task<int> QueryAsync(long id, CancellationToken ct)
    {
        var key = _cfg.ApiKey;
        try
        {
            Interlocked.Increment(ref _calls);
            RecordCall();
            var json = await _http.GetStringAsync(
                $"https://api.torn.com/user/{id}?selections=profile&key={key}", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            {
                var code = (int)J.Num(err, "code");
                var text = J.Str(err, "error") ?? "unknown";

                // Problems with the key or the rate limit are not the user's fault: pause, don't flag the user.
                if (code == 5)
                {
                    RegisterLimitHit(30_000);
                    return 30_000;
                }
                if (code is 2 or 10 or 13 or 18)
                {
                    Message = $"{DateTime.Now:HH:mm:ss}  API key problem (code {code}: {text}). Check config.json.";
                    return 60_000;
                }

                _store.ApplyError(id, $"Error {code}: {text}");
                return 0;
            }

            _store.ApplySuccess(id, root);
            Message = $"Last update: {DateTime.Now:HH:mm:ss}";
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Network problem: keep the old data, show it in the footer, try again at the normal interval.
            Message = $"{DateTime.Now:HH:mm:ss}  Request failed: {ex.Message.Replace(key, "***")}";
            return 0;
        }
    }
}
