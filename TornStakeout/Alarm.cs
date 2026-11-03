using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TornStakeout;

// Desktop notifications: macOS (osascript, built in), Linux (notify-send). Anywhere else, or if the tool
// is missing, it falls back to the terminal bell.
static class Notifier
{
    public static void Send(string title, string message)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Run("osascript", "-e",
                    $"display notification \"{Escape(message)}\" with title \"{Escape(title)}\" sound name \"Glass\"");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Run("notify-send", title, message);
            else
                Console.Write('\a');
        }
        catch
        {
            Console.Write('\a');
        }
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);   // not awaited: the notification tool exits by itself
    }
}

// The [A] alarm: while on, sends one notification for each hospital or jail release that is AlarmLeadSeconds or less away,
// and one when a player becomes active (Okay) earlier than expected, or after being abroad / travelling.
class Alarm
{
    readonly AppConfig _cfg;
    readonly DataStore _store;
    readonly HashSet<(long id, long until)> _alerted = new();   // releases already announced (a new timer counts again)

    public bool Enabled { get; private set; }

    public Alarm(AppConfig cfg, DataStore store) { _cfg = cfg; _store = store; }

    public void Toggle()
    {
        if (Enabled) Enabled = false;
        else TurnOn();
    }

    public void TurnOn()
    {
        Enabled = true;
        // a notification every time the alarm goes on, so a blocked or missing one shows up now and not mid-event
        Notifier.Send("Torn Stakeout", $"Alarm on: you will be notified {_cfg.AlarmLeadSeconds}s before a hospital or jail release.");
    }

    // Call about once a second.
    public void Check()
    {
        var early = _store.TakeActivations();   // always drained, so events from while the alarm was off are never announced later
        if (!Enabled) return;

        if (early.Count > 0)
            Notifier.Send("Torn Stakeout", string.Join("; ", early));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fresh = new List<(string name, long until, string place)>();
        foreach (var p in _store.ReleasingSoon(_cfg.AlarmLeadSeconds))
            if (_alerted.Add((p.id, p.until)) && !p.quiet)    // quiet ones are marked as announced, without a notification
                fresh.Add((p.name, p.until, p.place));
        if (fresh.Count == 0) return;

        var message = fresh.Count == 1
            ? $"{fresh[0].name} leaves {fresh[0].place} in {Math.Max(0, fresh[0].until - now)}s"
            : $"{fresh.Count} players are released within {_cfg.AlarmLeadSeconds}s: {string.Join(", ", fresh.Select(f => f.name))}";
        Notifier.Send("Torn Stakeout", message);
    }
}
