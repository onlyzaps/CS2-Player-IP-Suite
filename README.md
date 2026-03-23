<p align="center">
  <h1 align="center">🛡️ CS2-IPBanPlugin</h1>
  <p align="center">
    A powerful IP ban management plugin for Counter-Strike 2 servers<br/>
    Built on <a href="https://github.com/roflmuffin/CounterStrikeSharp">CounterStrikeSharp</a>
  </p>
</p>

---

## ✨ Features

| Feature | Description |
|---------|-------------|
| **IP Ban Management** | Add, remove, list, and persist IP bans with full metadata (who, when, why) |
| **Interactive Player Menu** | Type `addip` with no args to get a numbered chat menu — select a player, enter a duration, done |
| **VPN / Datacenter Detection** | Instant local CIDR lookup against 200+ datacenter ranges + async [ip-api.com](http://ip-api.com) proxy/hosting checks |
| **VPN Ban Confirmation** | Warns admins when a target IP is detected as VPN/datacenter and prompts for confirmation |
| **Admin Connect Notifications** | Player name, SteamID, geolocation, and VPN status shown to admins in chat on every join (IP shown in console only) |
| **Connection History Log** | Every connection is logged to `history.json` with IP, SteamID, name, location, and VPN status — newest entries at the top |
| **Blocked Connection Log** | Banned players that attempt to connect are logged to `blocked_log.jsonl` |
| **Flexible Time Parsing** | `0` (permanent), `60` (minutes), or natural language like `1 day`, `2 weeks 3 days`, `6 months` |
| **Rich Ban Metadata** | Each ban stores the issuing admin, UTC timestamp, and reason — persisted as inline comments in `banned_ip.cfg` |
| **Admin-Only Output** | All plugin messages are visible only to players with `@css/ban` permission |
| **Clean Hot Reload** | Properly unloads listeners, clears state, and disposes resources |

---

## 📋 Requirements

- Counter-Strike 2 dedicated server
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (latest)
- .NET 8.0 runtime

---

## 🚀 Installation

1. **Build the plugin:**
   ```
   dotnet build -c Release
   ```
2. **Copy the output** from `bin/Release/net8.0/` to your CounterStrikeSharp plugins directory:
   ```
   csgo/addons/counterstrikesharp/plugins/IPBanPlugin/
   ```
3. **Restart** the server or hot-reload the plugin.

---

## 🔧 Admin Commands

> All commands require the **`@css/ban`** permission.
> Commands work in chat with or without `!` / `/` prefixes. Console commands use the `css_` prefix.

| Command | Usage | Description |
|---------|-------|-------------|
| `addip` | `addip <time> <ip> ["reason"]` | Ban an IP. No args opens the interactive player menu. |
| `banip` | — | Alias for `addip` |
| `ipban` | — | Alias for `addip` |
| `removeip` | `removeip <ip>` | Remove an IP ban |
| `listip` | `listip` | List all active IP bans with metadata |
| `writeip` | `writeip` | Force-write the ban list to `banned_ip.cfg` |
| `ipbanhelp` | `ipbanhelp` | Show the in-game help message |

### Time Format

| Input | Result |
|-------|--------|
| `0` | Permanent |
| `60` | 60 minutes |
| `1 day` | 1 day |
| `2 weeks 3 days` | 2 weeks and 3 days |
| `6 months` | 6 months |

**Supported units:** `minutes/min/m` · `hours/hr/h` · `days/d` · `weeks/w` · `months/mo` · `years/yr/y`

### Examples

```
addip 0 192.168.1.100 "cheating"        → Permanent ban with reason
addip 1 day 10.0.0.5 "toxic griefing"   → 1-day ban with quoted reason
addip 2 weeks 3 days 172.16.0.1         → 2 weeks 3 days, no reason
addip                                    → Opens interactive player menu
!removeip 192.168.1.100                  → Remove a ban
!listip                                  → Show all active bans
```

---

## 💬 Chat Interfaces

### Interactive Player Menu

Triggered by typing `addip` with no arguments.

**Step 1 — Player Selection:**
```
[IPBan] Select a player to ban (or type 'cancel'):
[1] PlayerOne - 192.168.1.10
[2] PlayerTwo - 10.0.0.5
```

**Step 2 — Duration:**
```
[IPBan] Banning: PlayerOne (192.168.1.10)
Enter ban duration (or type 'cancel'):
Examples: 0 (permanent), 60 (60 min), 1 day, 2 weeks, 6 months
```

**Step 3 — VPN Confirmation** *(only if VPN detected):*
```
[IPBan] WARNING: 192.168.1.10 is detected as [VPN/DATACENTER].
Banning a VPN IP may be ineffective. Proceed? Type yes or no.
```

### Ban List (`!listip`)

```
[IPBan] --- Banned IPs (2) ---
 AdminName [192.168.1.100] 2026-03-14 12:00:00Z - cheating  (permanent)
 AdminName [10.0.0.5] 2026-03-14 08:30:00Z - griefing  (1 day(s) expires 2026-03-15 08:30:00Z)
```

---

## 🔔 Connect Notifications

When any player connects, admins are notified automatically:

**In chat** *(no IP shown for privacy):*
```
PlayerName | 76561198012345678 | City, Region, US | [No VPN]
```

**In console** *(full details including IP):*
```
PlayerName | 76561198012345678 | 192.168.1.10 | City, Region, US | [No VPN]
```

- Player name appears in **green**
- VPN status appears in **yellow** (`[No VPN]`) or **red** (`[VPN/DATACENTER]`, `[VPN/PROXY: Provider]`, `[DC: Provider]`)
- Banned players are kicked immediately and admins see a **red** `BLOCKED` message with the IP

---

## 🛡️ VPN / Datacenter Detection

Detection runs in two stages:

1. **Local CIDR lookup** *(instant)* — Checks against 200+ hardcoded datacenter/VPN provider ranges:
   - OVH, DigitalOcean, Vultr, Linode, Hetzner, AWS, GCP, Azure, Scaleway
   - NordVPN, Surfshark, ExpressVPN, PIA, M247, Choopa
2. **ip-api.com query** *(async, 5s timeout)* — Checks `proxy` and `hosting` flags. Only queried if the local lookup doesn't match.

---

## 📁 File Formats

### `banned_ip.cfg`

> `csgo/cfg/banned_ip.cfg`

Each line is a ban directive with metadata stored in an inline comment:

```
// This file is managed by IPBanPlugin. Do not edit while server is running.
addip 0 "192.168.1.100" //AdminName, 2026-03-14 12:00:00Z, cheating
addip 1440 "10.0.0.5" //AdminName, 2026-03-14 08:30:00Z, griefing
```

Comment format: `//BannedBy, UTC DateTime, Reason`

The Source engine reads this file on startup and ignores the `//` comments. The plugin manages reads and writes — it does **not** call the engine's `writeip` command, which would strip the metadata.

### `history.json`

> Plugin module directory (`addons/counterstrikesharp/plugins/IPBanPlugin/history.json`)

Logs every player connection — one JSON object per line, **newest entries at the top** of the file:

```json
{"timestamp":"2026-03-23T14:30:00.0000000Z","ip":"192.168.1.10","steamId":"76561198012345678","playerName":"PlayerOne","location":"City, Region, US","vpnTag":"[NO VPN]"}
{"timestamp":"2026-03-23T14:25:00.0000000Z","ip":"10.0.0.5","steamId":"76561198087654321","playerName":"PlayerTwo","location":"","vpnTag":"[VPN/DATACENTER]"}
```

### `blocked_log.jsonl`

> Plugin module directory (`addons/counterstrikesharp/plugins/IPBanPlugin/blocked_log.jsonl`)

Logs connection attempts by players whose IP is currently banned:

```json
{"timestamp":"2026-03-23T14:30:00.0000000Z","ip":"192.168.1.100","steamId":"76561198012345678","playerName":"BannedPlayer"}
```

---

## ⚙️ Technical Notes

- All state (`_menuStates`, `_vpnConfirmStates`) is cleaned up on player disconnect
- The ban list is protected by a lock for thread safety
- The `_unloaded` flag is `volatile` to ensure visibility across async threads
- Async VPN lookups marshal results back to the game thread via `Server.NextFrame()`
- `LoadBansFromFile()` is called before each new ban to pick up external changes and prevent duplicates
- Connection history is prepended (newest first) so the file can be tailed from the top without reading the entire file

---

## 📄 License

MIT
