# Torn Stakeout

Stakeout a list of players without flooding the Torn API with requests.

This is a vibe-coded app.

## Usage

This program will run on any platform with the .NET runtime installed.

Terminal command to run in the project's folder:

    dotnet run --project TornStakeout

## Configuration

The app will create any missing files on first run. The default settings should be sufficient, but it won't show anything useful until you specify the following two settings.

- users.txt - The list of players you want to watch.
- config.json - If your terminal supports clickable links, set "LinkMode" to "hyperlink", otherwise "attack" (attack URL only) or "profile" (profile URL only).

Display settings.
- `LinkMode`. Options are: "hyperlink" (clickable text), "attack" (attack URL only), "profile" (profile URL only) or "url" (both full URLs).
- `LastActionRedWithin`. Last action will show red if up to this value, e.g. "15m". Default: no colour.
- `LastActionYellowWithin`. Last action will show yellow if up to this value, e.g. "7d". Default: no colour.

Below is an overview of all remaining configuration settings.

- `ApiKey`. Your Torn API key. Public Only access is sufficient. Place this in 'local.json', or TORN_API_KEY environment variable.
- `CallsPerTick`. API calls made at the same time, once per tick.
- `TickIntervalSeconds`. The delay between every tick (minimum 1).
- `MaxCallsPerMinute`. The hard budget over any 60s duration. The app will pause for 30s whenever you exceed the Torn API limit.
- `OkayRecheckSeconds`. Re-check "Okay" players this often.
- `BusyRecheckSeconds`. Safety re-check for players in hospital / jail.
- `ReleaseRecheckDelaySeconds`. Wait this long after a hospital / jail timer ends before re-checking.
- `AwayRecheckSeconds`. Re-check abroad / travelling players.
- `ErrorRetrySeconds`. Retry users whose last call returned an API error.

Experimental settings.

- `ActiveBusyRecheckSeconds`. Re-check for hospital / jail players who were active recently (disabled by default).
- `ActiveWithinMinutes`. "Recently active" = last action within this many minutes, 0 to disable (default 0).

Configuration priority.

- If no configuration files are present it will read the default values in the code.
- If "config.json" is present it will use the values from this file instead of the default values.
- Anything in "local.json" will override "config.json". This is intended for local-only values, like the API key.
- The TORN_API_KEY environment variable will alway take priority if specified.

## Features

This app is most useful if you have a list of hospitalised targets. For example, events like Trick or Treat, or bounty hunting.

If you have a lot of "okay" players it will run through API calls much faster. All timers are configurable, so you can adjust it to your taste.
