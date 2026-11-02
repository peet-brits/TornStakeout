using System.Text;
using TornStakeout;

const string Esc = "\u001b";

var dir = Directory.GetCurrentDirectory();      // `dotnet run` starts in the project folder
var configPath = Path.Combine(dir, "config.json");
var usersPath = Path.Combine(dir, "users.txt");
var dataPath = Path.Combine(dir, "data.json");

var cfg = AppConfig.Load(configPath);
if (string.IsNullOrWhiteSpace(cfg.ApiKey) || cfg.ApiKey.StartsWith("PUT_YOUR"))
{
    Console.WriteLine("No API key found. Create a file called local.json in this folder containing:\n" +
                      "  { \"ApiKey\": \"your-key-here\" }\n" +
                      "Then run again. (You can also set the TORN_API_KEY environment variable.)");
    return;
}

var store = new DataStore(dataPath);
store.Load();
store.ResetOkayPlayers();       // startup only: saved "okay" players show as "waiting..." until queried again

if (!File.Exists(usersPath))
    File.WriteAllText(usersPath, "# One user per line: \"Name [1234]\" or just \"1234\". Lines starting with # are ignored.\n");
store.MergeUserList(usersPath, out var skippedAtStart);
if (store.Count == 0)
{
    Console.WriteLine($"No users yet. Add them to:\n  {usersPath}\n(one per line, like: Sample [1234]  or  1234)");
    return;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var poller = new Poller(cfg, store);
var pollTask = Task.Run(() => poller.RunAsync(cts.Token));
var alarm = new Alarm(cfg, store);              // off until you press A

var notice = skippedAtStart > 0 ? $"{skippedAtStart} line(s) in users.txt were not understood and skipped" : "";
var noticeTime = DateTime.Now;

Console.OutputEncoding = Encoding.UTF8;
Console.Write($"{Esc}[2J{Esc}[?25l");          // clear screen, hide cursor
try
{
    while (!cts.IsCancellationRequested)
    {
        if (notice != "" && (DateTime.Now - noticeTime).TotalSeconds > 15) notice = "";
        alarm.Check();
        Console.Write(Screen.Build(cfg, store, poller, notice, alarm.Enabled));

        for (var i = 0; i < 10 && !cts.IsCancellationRequested; i++)   // redraw every second, check keys every 100 ms
        {
            while (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                var key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.Q) cts.Cancel();
                else if (key == ConsoleKey.A) alarm.Toggle();
                else if (key == ConsoleKey.R)
                {
                    try
                    {
                        store.MergeUserList(usersPath, out var skipped);
                        notice = $"users.txt reloaded: {store.Count} users" + (skipped > 0 ? $", {skipped} line(s) skipped" : "");
                    }
                    catch (Exception ex) { notice = "Could not read users.txt: " + ex.Message; }
                    noticeTime = DateTime.Now;
                }
            }
            await Task.Delay(100);
        }
    }
}
finally
{
    Console.Write($"{Esc}[0m{Esc}[?25h\n");     // reset colours, show cursor
}
await pollTask;
store.SaveIfDirty();

// ---------------------------------------------------------------- drawing

static class Screen
{
    const string Esc = "\u001b";

    static string Paint(string code, string text) => $"{Esc}[{code}m{text}{Esc}[0m";

    static string Fit(string s, int width) =>
        s.Length > width ? s[..(width - 1)] + "…" : s.PadRight(width);

    static string StatusColor(string apiColor) => apiColor switch
    {
        "green" => "32",
        "red" => "31",
        "blue" => "36",
        _ => "0"
    };

    // red if the last action is recent enough, otherwise yellow, otherwise no colour (bounty rows have no age)
    static string AgeColor(AppConfig cfg, Row r) =>
        r.AgeSeconds < 0 ? "0"
        : r.AgeSeconds <= cfg.LastActionRedSeconds ? "31"
        : r.AgeSeconds <= cfg.LastActionYellowSeconds ? "33"
        : "0";

    static string Links(AppConfig cfg, long id)
    {
        var profile = $"https://www.torn.com/profiles.php?XID={id}";
        var attack = $"https://www.torn.com/page.php?sid=attack&user2ID={id}";
        if (cfg.LinkMode.Equals("profile", StringComparison.OrdinalIgnoreCase)) return profile;
        if (cfg.LinkMode.Equals("attack", StringComparison.OrdinalIgnoreCase)) return attack;
        if (cfg.LinkMode.Equals("url", StringComparison.OrdinalIgnoreCase))
            return $"{profile}  {attack}";
        return $"{Esc}]8;;{profile}{Esc}\\profile{Esc}]8;;{Esc}\\ {Esc}]8;;{attack}{Esc}\\attack{Esc}]8;;{Esc}\\";
    }

    public static string Build(AppConfig cfg, DataStore store, Poller poller, string notice, bool alarmOn)
    {
        int height;
        try { height = Console.WindowHeight; } catch { height = 40; }

        var (main, attention) = store.Snapshot();
        var sb = new StringBuilder();
        var used = 0;                                   // screen lines written so far
        void Line(string s = "") { sb.Append(s).Append(Esc).Append("[K\n"); used++; }

        sb.Append($"{Esc}[H");
        var saved = store.LastSaved?.ToLocalTime().ToString("HH:mm:ss") ?? "never";
        Line(Paint("1", "Torn Stakeout") +
             $"  {DateTime.Now:HH:mm:ss}   users: {store.Count}   calls: {poller.TotalCalls} ({poller.CallsLastMinute}/{cfg.MaxCallsPerMinute} per min)   saved: {saved}   " +
             Paint("90", "[R] reload  ") +
             (alarmOn ? Paint("30;43", "[A] alarm") : Paint("90", "[A] alarm")) +     // highlighted while the alarm is on
             Paint("90", "  [Q] quit"));
        Line(Paint("90", poller.Message) + (notice != "" ? "   " + Paint("33", notice) : ""));
        var pause = poller.PauseSecondsLeft;
        if (pause > 0) Line(Paint("1;31", $"PAUSED: too many requests, resuming in {pause}s"));
        if (poller.LimitNote != "") Line(Paint("31", poller.LimitNote));
        Line();

        // Rows left for data = window height - header lines so far - 1 spare line (so the last newline doesn't scroll)
        // - the STATUS title and column header (2) - the attention section's blank line, title and column header (3),
        // which only exist when there is something to show there.
        var avail = Math.Max(2, height - used - 1 - 2 - (attention.Count > 0 ? 3 : 0));

        int mainShown, attShown;
        if (attention.Count == 0)
        {
            // nothing in the second list: the status list gets every row (one is kept for "... more" if it is cut off)
            mainShown = main.Count <= avail ? main.Count : Math.Max(0, avail - 1);
            attShown = 0;
        }
        else
        {
            // the attention list is guaranteed up to 6 rows; the status list gets the rest
            var attWant = Math.Min(attention.Count, 6);
            var mainRoom = Math.Max(1, avail - attWant);
            mainShown = main.Count <= mainRoom ? main.Count : Math.Max(0, mainRoom - 1);
            var attRoom = Math.Max(0, avail - mainShown - (main.Count > mainShown ? 1 : 0));
            attShown = attention.Count <= attRoom ? attention.Count : Math.Max(0, attRoom - 1);
        }

        Line(Paint("1", $"STATUS  ({main.Count})"));
        Line(Paint("90", Fit("Name", 16) + " " + Fit("Location", 9) + " " + "Left".PadLeft(7) + " " +
                         Fit("Life", 10) + " " + Fit("Last action", 15) + " Links"));
        foreach (var r in main.Take(mainShown))
        {
            Line(Fit(r.Name, 16) + " " +
                 Paint(StatusColor(r.Color), Fit(r.Location, 9)) + " " +
                 r.Secs.PadLeft(7) + " " +
                 Fit(r.Life, 10) + " " +
                 Paint(AgeColor(cfg, r), Fit(r.LastAction, 15)) + " " +
                 Links(cfg, r.Id));
        }
        if (main.Count > mainShown) Line(Paint("90", $"... {main.Count - mainShown} more (make the window taller)"));

        if (attention.Count > 0)
        {
            Line();
            Line(Paint("1", $"NEEDS ATTENTION  ({attention.Count})"));
            Line(Paint("90", Fit("Name", 16) + " " + Fit("Reason", 28) + " " + Fit("Last action", 15) + " Links"));
            foreach (var r in attention.Take(attShown))
            {
                Line(Fit(r.Name, 16) + " " +
                     Paint(r.IsError ? "31" : "36", Fit(r.Reason, 28)) + " " +
                     Paint(AgeColor(cfg, r), Fit(r.LastAction, 15)) + " " +
                     Links(cfg, r.Id));
            }
            if (attention.Count > attShown) Line(Paint("90", $"... {attention.Count - attShown} more (make the window taller)"));
        }

        sb.Append($"{Esc}[J");     // clear anything left below
        return sb.ToString();
    }
}
