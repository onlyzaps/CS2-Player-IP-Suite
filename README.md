# IPBan Plugin for Counter-Strike 2

A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin that provides comprehensive IP ban management for CS2 servers. Bans are synced to `banned_ip.cfg` with rich metadata (who banned, when, and why), and the plugin enforces bans on connect with built-in VPN/datacenter detection and account association tracking.

## Features

- **IP Ban Management** — Add, remove, list, and persist IP bans with full metadata.
- **Interactive Player Menu** — No-args `addip` opens a numbered chat menu to select a connected player, enter a duration, and confirm.
- **VPN / Datacenter Detection** — Instant local CIDR lookup against 200+ datacenter ranges (OVH, AWS, GCP, Azure, DigitalOcean, Vultr, Hetzner, etc.) plus async [ip-api.com](http://ip-api.com) queries for proxy/hosting detection.
- **VPN Ban Confirmation** — If a target IP is detected as VPN/datacenter, the admin is warned and prompted to confirm before banning.
- **Account Association Database** — Tracks SteamID ↔ IP relationships in `ip_accounts.json`. Uses BFS flood-fill for transitive association lookup (if A shared an IP with B, and B with C, all three are linked).
- **Admin Connect Notifications** — On player connect, admins see the player's name, SteamID, IP, VPN status, and any associated accounts.
- **Rich Ban Metadata** — Each ban records who issued it, when (UTC), and the reason. Comments are stored inline in `banned_ip.cfg`.
- **Flexible Time Parsing** — Supports plain minutes (`60`), or natural language (`1 day`, `2 weeks 3 days`, `6 months`).
- **Admin-Only Output** — All plugin messages are visible only to players with `@css/ban` permission.
- **Clean Hot Reload** — Properly unloads all listeners, clears state, and disposes resources.

## Requirements

- Counter-Strike 2 dedicated server
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (latest)
- .NET 8.0 runtime

## Installation

1. Build the plugin:
   ```
   dotnet build -c Release
   ```
2. Copy the output from `bin/Release/net8.0/` to your CounterStrikeSharp plugins directory:
   ```
   csgo/addons/counterstrikesharp/plugins/IPBanPlugin/
   ```
3. Restart the server or hot-reload the plugin.

## Admin Commands

All commands require the `@css/ban` permission. Commands can be typed in chat with or without a `!` or `/` prefix (e.g. `addip`, `!addip`, `/addip` all work). Console commands use the `css_` prefix.

| Command | Usage | Description |
|---------|-------|-------------|
| `addip` | `addip <time> <ip> ["reason"]` | Ban an IP address. No args opens the interactive player menu. |
| `banip` | — | Alias for `addip`. |
| `ipban` | — | Alias for `addip`. |
| `removeip` | `removeip <ip>` | Remove an IP ban. |
| `listip` | `listip` | List all active IP bans with metadata. |
| `writeip` | `writeip` | Force-write the ban list to `banned_ip.cfg`. |
| `ipbanhelp` | `ipbanhelp` | Show the in-game help message. |
| `help` | `help` | Also shows IPBan help (for admins). |

### Time Format

The `<time>` parameter accepts:

- `0` — Permanent ban
- Plain number — Minutes (e.g. `60` = 1 hour)
- Natural language — `1 day`, `2 weeks`, `6 months`, `1 year`
- Combined — `1 day 12 hours`, `2 weeks 3 days`

Supported units: `minutes/min/m`, `hours/hr/h`, `days/d`, `weeks/w`, `months/mo`, `years/yr/y`

### Examples

```
addip 0 192.168.1.100 "cheating"          → Permanent ban with reason
addip 1 day 10.0.0.5 "toxic griefing"     → 1-day ban with quoted reason
addip 2 weeks 3 days 172.16.0.1           → 2 weeks 3 days, no reason
addip                                      → Opens interactive player menu
!addip 0 10.0.0.1 "reason here"            → Also works with ! prefix
removeip 192.168.1.100                     → Remove the ban
listip                                     → Show all bans
```

## Chat Interfaces

### Help (`help` / `ipbanhelp`)

```
 --- IPBan Commands ---
 addip <time> <ip> ["reason"] - Ban an IP (or no args for player menu)
 banip / ipban - Aliases for addip
 removeip <ip> - Unban an IP address
 listip - List all active IP bans
 writeip - Save ban list to banned_ip.cfg
 ipbanhelp - Show this help message
 Time examples: 0 (permanent), 60 (60 min), 1 day, 2 weeks, 6 months
```

### Interactive Player Menu (`!addip` with no args)

**Step 1 — Player Selection:**
```
 [IPBan] Select a player to ban (or type 'cancel'):
 [1] PlayerOne - 192.168.1.10
 [2] PlayerTwo - 10.0.0.5
 [3] PlayerThree - 172.16.0.1
```
Type a number to select, or `cancel` to abort.

**Step 2 — Duration:**
```
 [IPBan] Banning: PlayerOne (192.168.1.10)
 Enter ban duration (or type 'cancel'):
 Examples: 0 (permanent), 60 (60 min), 1 day, 2 weeks, 6 months
```

**Step 3 (conditional) — VPN Confirmation:**
```
 [IPBan] WARNING: 192.168.1.10 is detected as [VPN/DATACENTER].
 Banning a VPN IP may be ineffective. Proceed? Type yes or no.
```

### Ban List (`!listip`)

```
 [IPBan] --- Banned IPs (3) ---
  AdminName [192.168.1.100] 2026-03-14 12:00:00Z - cheating  (permanent)
  AdminName [10.0.0.5] 2026-03-14 08:30:00Z - griefing  (1 day(s) expires 2026-03-15 08:30:00Z)
  Unknown [172.16.0.1] 2026-03-13 20:00:00Z - No reason  (2 week(s))
```

### Player Connect Notification (automatic)

When a player connects, all admins see (with colored text in-game):
```
 PlayerName | 76561198012345678 | 192.168.1.10 | [No VPN]
```
- **Player name** appears in green
- **VPN status** appears in yellow (`[No VPN]`) or red (`[VPN/DATACENTER]`, `[VPN/PROXY]`)

If the player has associated alt accounts, a second line appears with no prefix:
```
 [AltAccount1, AltAccount2]
```
Alt account names appear in blue.

## File Formats

### `banned_ip.cfg`

Located at `csgo/cfg/banned_ip.cfg`. Each line contains the ban plus a `//` comment with metadata:

```
// This file is managed by IPBanPlugin. Do not edit while server is running.
addip 0 "192.168.1.100" //AdminName, 2026-03-14 12:00:00Z, cheating
addip 1440 "10.0.0.5" //AdminName, 2026-03-14 08:30:00Z, griefing
addip 20160 "172.16.0.1" //CONSOLE, 2026-03-13 20:00:00Z,
```

The comment format is: `//BannedBy, UTC DateTime, Reason`

The plugin reads and writes this file. The Source engine also reads it on startup (the `//` comments are treated as inline comments and ignored by the engine parser). The plugin does **not** call the engine's `writeip` command, which would overwrite comments.

### `ip_accounts.json`

Located in the plugin's module directory. Tracks which SteamIDs have connected from which IPs (VPN IPs are excluded):

```json
{
  "IpToAccounts": {
    "192.168.1.10": [
      { "SteamId": 76561198012345678, "Name": "PlayerOne" },
      { "SteamId": 76561198087654321, "Name": "AltAccount" }
    ]
  }
}
```

## VPN Detection

Detection runs in two stages:

1. **Local CIDR lookup** (instant) — Checks against 200+ hardcoded datacenter/VPN provider ranges covering OVH, DigitalOcean, Vultr, Linode, Hetzner, AWS, GCP, Azure, Scaleway, NordVPN, PIA, and more.
2. **ip-api.com query** (async, 5s timeout) — Checks `proxy` and `hosting` flags. Only used if the local lookup doesn't match.

VPN IPs are excluded from the account association database to prevent false associations through shared VPN exit nodes.

## Technical Notes

- All state dictionaries (`_menuStates`, `_vpnConfirmStates`) are cleaned up on player disconnect.
- The ban list (`_bans`) is protected by a lock for thread safety. File I/O builds a temporary list and swaps it atomically.
- The `_unloaded` flag is `volatile` to ensure visibility across async Task threads.
- Async VPN lookups marshal results back to the game thread via `Server.NextFrame()`.
- `LoadBansFromFile()` is called before each new ban to pick up any external changes and prevent duplicates.

## License

MIT
