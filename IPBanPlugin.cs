using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

namespace IPBanPlugin;

public sealed class IPBanEntry
{
    public string IpAddress { get; set; } = string.Empty;
    public int Minutes { get; set; } // 0 = permanent
    public DateTime BannedAt { get; set; } = DateTime.UtcNow;
    public string BannedBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    public bool IsExpired =>
        Minutes > 0 && DateTime.UtcNow > BannedAt.AddMinutes(Minutes);
}

// Tracks an admin going through the interactive ban flow
internal sealed class BanMenuState
{
    public List<(string Name, string Ip)> Players { get; set; } = new();
    public string? SelectedIp { get; set; }
    public string? SelectedName { get; set; }
    public bool AwaitingTime { get; set; }
    public bool AwaitingVpnConfirm { get; set; }
    public int PendingMinutes { get; set; }
    public string PendingReason { get; set; } = "Banned via menu";
}

// Tracks a direct-ban VPN confirmation (admin used full args)
internal sealed class VpnConfirmState
{
    public string Ip { get; set; } = string.Empty;
    public int Minutes { get; set; }
    public string Reason { get; set; } = string.Empty;
}

// A single account record in the IP association database
internal sealed class AccountRecord
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = string.Empty;
}

// The persistent IP → accounts database
internal sealed class IpAccountDatabase
{
    // IP address → list of accounts that have used that IP (non-VPN only)
    public Dictionary<string, List<AccountRecord>> IpToAccounts { get; set; } = new();
}

public sealed partial class IPBanPlugin : BasePlugin
{
    public override string ModuleName => "IPBanPlugin";
    public override string ModuleVersion => "2.0.1";
    public override string ModuleAuthor => "VinSix";
    public override string ModuleDescription => "Manages IP bans via addip/removeip/listip with banned_ip.cfg sync.";

    private const string ColorDefault = "\x01";
    private const string ColorRed = "\x02";
    private const string ColorGreen = "\x04";
    private const string ColorYellow = "\x09";
    private const string ColorBlue = "\x0B";

    private readonly List<IPBanEntry> _bans = new();
    private readonly object _lock = new();
    private string _bannedIpCfgPath = string.Empty;
    private string _accountDbPath = string.Empty;
    private string _blockedLogPath = string.Empty;
    private HttpClient? _httpClient;
    private volatile bool _unloaded;

    // Pinned delegates to prevent GC from collecting callbacks used by native code
    private Listeners.OnClientConnected _onConnectedDelegate = null!;
    private Listeners.OnClientDisconnect _onDisconnectedDelegate = null!;
    private CommandInfo.CommandListenerCallback _onChatDelegate = null!;

    // Interactive menu state per admin slot
    private readonly Dictionary<int, BanMenuState> _menuStates = new();

    // Direct-ban VPN confirmation state per admin slot
    private readonly Dictionary<int, VpnConfirmState> _vpnConfirmStates = new();

    // IP ↔ Account association database
    private IpAccountDatabase _accountDb = new();
    private readonly object _dbLock = new();

    // Known datacenter / VPN provider CIDR ranges (sample of major providers)
    private static readonly (uint Network, uint Mask)[] DatacenterCidrs = BuildCidrTable(new[]
    {
        // OVH
        "51.38.0.0/16", "51.68.0.0/16", "51.77.0.0/16", "51.78.0.0/16",
        "51.79.0.0/16", "51.89.0.0/16", "51.91.0.0/16", "51.161.0.0/16",
        "51.178.0.0/16", "51.195.0.0/16", "51.210.0.0/16", "51.222.0.0/16",
        "54.36.0.0/16", "54.37.0.0/16", "54.38.0.0/16", "91.134.0.0/16",
        "92.222.0.0/16", "135.125.0.0/16", "137.74.0.0/16", "139.99.0.0/16",
        "141.94.0.0/16", "142.44.0.0/16", "144.217.0.0/16", "145.239.0.0/16",
        "147.135.0.0/16", "148.113.0.0/16", "149.56.0.0/16", "158.69.0.0/16",
        "167.114.0.0/16", "176.31.0.0/16", "178.32.0.0/16", "185.12.32.0/22",
        "188.165.0.0/16", "192.95.0.0/16", "192.99.0.0/16", "198.27.0.0/16",
        "198.50.0.0/16", "198.100.0.0/16", "198.245.0.0/16",
        // DigitalOcean
        "104.131.0.0/16", "104.236.0.0/16", "107.170.0.0/16", "128.199.0.0/16",
        "134.209.0.0/16", "137.184.0.0/16", "138.68.0.0/16", "138.197.0.0/16",
        "139.59.0.0/16", "142.93.0.0/16", "143.110.0.0/16", "143.198.0.0/16",
        "144.126.0.0/16", "146.190.0.0/16", "147.182.0.0/16", "157.230.0.0/16",
        "157.245.0.0/16", "159.65.0.0/16", "159.89.0.0/16", "159.203.0.0/16",
        "161.35.0.0/16", "162.243.0.0/16", "163.47.10.0/24", "164.90.0.0/16",
        "164.92.0.0/16", "165.22.0.0/16", "165.227.0.0/16", "167.71.0.0/16",
        "167.172.0.0/16", "170.64.0.0/16", "174.138.0.0/16", "178.62.0.0/16",
        "178.128.0.0/16", "188.166.0.0/16", "192.81.208.0/20", "198.199.64.0/18",
        "203.0.113.0/24", "206.189.0.0/16", "209.97.0.0/16", "45.55.0.0/16",
        "46.101.0.0/16", "64.225.0.0/16", "68.183.0.0/16",
        // Vultr
        "45.32.0.0/16", "45.63.0.0/16", "45.76.0.0/16", "45.77.0.0/16",
        "64.176.0.0/16", "64.227.0.0/16", "66.42.0.0/16", "78.141.0.0/16",
        "95.179.0.0/16", "104.156.0.0/16", "104.207.0.0/16", "108.61.0.0/16",
        "136.244.0.0/16", "140.82.0.0/16", "149.28.0.0/16", "155.138.0.0/16",
        "185.92.220.0/22", "199.247.0.0/16", "207.148.0.0/16", "208.167.0.0/16",
        "209.250.0.0/16", "216.128.0.0/16", "217.69.0.0/16",
        // Linode / Akamai Connected Cloud
        "45.33.0.0/16", "45.56.0.0/16", "45.79.0.0/16", "50.116.0.0/16",
        "66.175.208.0/20", "69.164.192.0/18", "72.14.176.0/20", "74.207.224.0/19",
        "96.126.96.0/19", "97.107.128.0/17", "103.2.16.0/22", "109.237.24.0/22",
        "139.144.0.0/16", "139.162.0.0/16", "143.42.0.0/16", "170.187.0.0/16",
        "172.104.0.0/16", "172.105.0.0/16", "173.255.192.0/18", "176.58.0.0/16",
        "178.79.128.0/17", "194.195.208.0/21", "198.58.96.0/19",
        // Hetzner
        "5.9.0.0/16", "23.88.0.0/16", "46.4.0.0/16", "49.12.0.0/16",
        "65.21.0.0/16", "65.108.0.0/16", "78.46.0.0/16", "78.47.0.0/16",
        "85.10.192.0/18", "88.99.0.0/16", "88.198.0.0/16", "91.107.128.0/17",
        "95.216.0.0/16", "95.217.0.0/16", "116.202.0.0/16", "116.203.0.0/16",
        "128.140.0.0/16", "135.181.0.0/16", "136.243.0.0/16", "138.201.0.0/16",
        "142.132.0.0/16", "144.76.0.0/16", "148.251.0.0/16", "157.90.0.0/16",
        "159.69.0.0/16", "162.55.0.0/16", "167.235.0.0/16", "168.119.0.0/16",
        "176.9.0.0/16", "178.63.0.0/16", "188.40.0.0/16", "195.201.0.0/16",
        "213.133.96.0/19", "213.239.192.0/18",
        // AWS EC2 (partial – major ranges)
        "3.0.0.0/9", "13.32.0.0/12", "13.48.0.0/13", "13.56.0.0/14",
        "15.164.0.0/15", "15.188.0.0/14", "18.128.0.0/9", "34.192.0.0/10",
        "35.152.0.0/13", "35.160.0.0/11", "44.192.0.0/10", "50.16.0.0/14",
        "52.0.0.0/11", "54.64.0.0/11", "54.144.0.0/12", "54.160.0.0/11",
        "54.192.0.0/12", "54.208.0.0/13", "54.216.0.0/14", "54.224.0.0/12",
        "54.240.0.0/12",
        // Google Cloud (partial)
        "34.64.0.0/11", "34.96.0.0/12", "34.112.0.0/14", "34.116.0.0/14",
        "34.120.0.0/13", "34.128.0.0/10", "35.184.0.0/13", "35.192.0.0/12",
        "35.208.0.0/12", "35.224.0.0/12", "35.240.0.0/13",
        // Azure (partial)
        "13.64.0.0/11", "13.96.0.0/13", "13.104.0.0/14", "20.0.0.0/11",
        "20.33.0.0/16", "20.34.0.0/15", "20.36.0.0/14", "20.40.0.0/13",
        "20.48.0.0/12", "20.64.0.0/10", "20.128.0.0/16", "40.64.0.0/10",
        "51.104.0.0/15", "51.120.0.0/14", "52.96.0.0/12", "52.112.0.0/14",
        "52.120.0.0/14", "52.124.0.0/14", "52.136.0.0/13", "52.144.0.0/14",
        "52.148.0.0/14", "52.152.0.0/13", "52.160.0.0/11", "52.224.0.0/11",
        "104.40.0.0/13", "104.208.0.0/13",
        // Choopa / GameServers
        "66.55.128.0/17", "108.61.0.0/16", "209.222.0.0/16",
        // M247 / VPN providers
        "185.56.80.0/22", "185.153.176.0/22", "185.245.84.0/22",
        "193.27.12.0/22", "193.138.218.0/24", "196.240.52.0/22",
        // NordVPN / Surfshark / ExpressVPN common ranges
        "82.102.16.0/20", "185.216.34.0/23", "185.159.156.0/22",
        "194.187.249.0/24", "194.36.108.0/22",
        // PIA
        "209.222.0.0/16", "199.116.112.0/21",
        // Scaleway
        "51.15.0.0/16", "51.158.0.0/15", "62.210.0.0/16", "163.172.0.0/16",
        "195.154.0.0/16", "212.47.224.0/19", "212.129.0.0/18",
    });

    // ═══════════════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════════════
    public override void Load(bool hotReload)
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _bannedIpCfgPath = Path.Combine(Server.GameDirectory, "csgo", "cfg", "banned_ip.cfg");
        _accountDbPath = Path.Combine(ModuleDirectory, "ip_accounts.json");
        _blockedLogPath = Path.Combine(ModuleDirectory, "blocked_log.jsonl");
        LoadBansFromFile();
        LoadAccountDatabase();

        _onConnectedDelegate = OnClientConnected;
        _onDisconnectedDelegate = OnClientDisconnected;
        _onChatDelegate = OnPlayerChat;

        RegisterListener<Listeners.OnClientConnected>(_onConnectedDelegate);
        RegisterListener<Listeners.OnClientDisconnect>(_onDisconnectedDelegate);

        AddCommandListener("say", _onChatDelegate, HookMode.Post);
        AddCommandListener("say_team", _onChatDelegate, HookMode.Post);
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;

        // Remove listeners
        RemoveListener<Listeners.OnClientConnected>(_onConnectedDelegate);
        RemoveListener<Listeners.OnClientDisconnect>(_onDisconnectedDelegate);
        RemoveCommandListener("say", _onChatDelegate, HookMode.Post);
        RemoveCommandListener("say_team", _onChatDelegate, HookMode.Post);

        // Clear collections
        _menuStates.Clear();
        _vpnConfirmStates.Clear();
        lock (_lock) { _bans.Clear(); }
        lock (_dbLock) { _accountDb = new IpAccountDatabase(); }

        // Dispose managed resources
        _httpClient?.Dispose();
        _httpClient = null;
    }

    // ═══════════════════════════════════════════════════
    //  Commands – addip / banip / ipban
    // ═══════════════════════════════════════════════════

    [ConsoleCommand("css_addip", "Ban an IP address. Usage: css_addip <time> <ip>  — or no args for player menu.")]
    [RequiresPermissions("@css/ban")]
    public void OnAddIpCommand(CCSPlayerController? player, CommandInfo command) => HandleBanCommand(player, command);

    [ConsoleCommand("css_banip", "Alias for css_addip.")]
    [RequiresPermissions("@css/ban")]
    public void OnBanIpCommand(CCSPlayerController? player, CommandInfo command) => HandleBanCommand(player, command);

    [ConsoleCommand("css_ipban", "Alias for css_addip.")]
    [RequiresPermissions("@css/ban")]
    public void OnIpBanCommand(CCSPlayerController? player, CommandInfo command) => HandleBanCommand(player, command);

    private void HandleBanCommand(CCSPlayerController? player, CommandInfo command)
    {
        // If typed from RCON / server console with full args, execute directly
        if (player == null)
        {
            HandleDirectBan(player, command);
            return;
        }

        // If args provided, do a direct ban
        if (command.ArgCount >= 3)
        {
            HandleDirectBan(player, command);
            return;
        }

        // No args → open interactive player picker menu
        ShowPlayerMenu(player);
    }

    private void HandleDirectBan(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 3)
        {
            ReplyToAdmin(player, $"{ColorDefault}[IPBan] Usage: css_addip <time> <ip> [reason]");
            return;
        }

        // Find the IP argument – scan from arg index 2 onward for the first valid IP
        string ip = "";
        int ipArgIndex = -1;
        for (int i = 2; i < command.ArgCount; i++)
        {
            if (IsValidIpAddress(command.GetArg(i)))
            {
                ip = command.GetArg(i);
                ipArgIndex = i;
                break;
            }
        }

        // Fallback: last arg must be the IP (original behaviour)
        if (ipArgIndex < 0)
        {
            ip = command.GetArg(command.ArgCount - 1);
            if (!IsValidIpAddress(ip))
            {
                ReplyToAdmin(player, $"{ColorDefault}[IPBan] Invalid IP address: {ip}");
                return;
            }
            ipArgIndex = command.ArgCount - 1;
        }

        // Reconstruct time string from args between command and IP
        var timeParts = new List<string>();
        for (int i = 1; i < ipArgIndex; i++)
            timeParts.Add(command.GetArg(i));
        string timeStr = string.Join(" ", timeParts);

        int minutes = ParseTimeToMinutes(timeStr);
        if (minutes < 0)
        {
            ReplyToAdmin(player, $"{ColorDefault}[IPBan] Invalid time: {timeStr}. Examples: 0, 60, 1 day, 2 weeks, 6 months");
            return;
        }

        // Everything after the IP is the optional reason
        var reasonParts = new List<string>();
        for (int i = ipArgIndex + 1; i < command.ArgCount; i++)
            reasonParts.Add(command.GetArg(i));
        string reason = reasonParts.Count > 0 ? string.Join(" ", reasonParts) : string.Empty;

        string adminName = player?.PlayerName ?? "CONSOLE";

        // For RCON / server console, skip VPN check and ban immediately
        if (player == null)
        {
            ExecuteBan(null, ip, minutes, reason, adminName);
            return;
        }

        // Async VPN check before banning
        int adminSlot = player.Slot;
        _ = Task.Run(async () =>
        {
            if (_unloaded) return;
            var (vpnTag, _) = await CheckVpnAsync(ip);
            bool isVpn = vpnTag != "[NO VPN]";

            Server.NextFrame(() =>
            {
                if (_unloaded) return;
                var admin = Utilities.GetPlayerFromSlot(adminSlot);
                if (admin == null || !admin.IsValid) return;
                if (isVpn)
                {
                    _vpnConfirmStates[admin.Slot] = new VpnConfirmState { Ip = ip, Minutes = minutes, Reason = reason };
                    admin.PrintToChat($" {ColorGreen}[IPBan]{ColorDefault} WARNING: {ip} is detected as {vpnTag}.");
                    admin.PrintToChat($" {ColorDefault}Banning a VPN IP may be ineffective. Proceed? Type {ColorGreen}yes{ColorDefault} or {ColorGreen}no{ColorDefault}.");
                }
                else
                {
                    ExecuteBan(admin, ip, minutes, reason, adminName);
                }
            });
        });
    }

    private void ExecuteBan(CCSPlayerController? admin, string ip, int minutes, string reason = "", string bannedBy = "")
    {
        LoadBansFromFile();

        lock (_lock)
        {
            var existing = _bans.FirstOrDefault(b => b.IpAddress == ip);
            if (existing != null)
            {
                string existDuration = existing.Minutes == 0 ? "permanent" : $"{existing.Minutes} min";
                ReplyToAdmin(admin, $"{ColorDefault}[IPBan] {ip} is already banned ({existDuration}). Use css_removeip first to change the ban.");
                return;
            }

            _bans.Add(new IPBanEntry
            {
                IpAddress = ip,
                Minutes = minutes,
                BannedAt = DateTime.UtcNow,
                BannedBy = bannedBy,
                Reason = reason
            });
        }

        Server.ExecuteCommand($"addip {minutes} {ip}");
        WriteBansToFile();
        KickBannedPlayers();

        string duration = minutes == 0 ? "permanently" : $"for {FormatMinutes(minutes)}";
        string reasonSuffix = string.IsNullOrEmpty(reason) ? "" : $" Reason: {reason}";
        NotifyAdmins($"{ColorGreen}[IPBan]{ColorDefault} Banned {ip} {duration}.{reasonSuffix}");
    }

    // ═══════════════════════════════════════════════════
    //  Commands – removeip
    // ═══════════════════════════════════════════════════

    [ConsoleCommand("css_removeip", "Unban an IP address. Usage: css_removeip <ip>")]
    [RequiresPermissions("@css/ban")]
    public void OnRemoveIpCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            ReplyToAdmin(player, $"{ColorDefault}[IPBan] Usage: css_removeip <ip>");
            return;
        }

        string ip = command.GetArg(1);
        if (!IsValidIpAddress(ip))
        {
            ReplyToAdmin(player, $"{ColorDefault}[IPBan] Invalid IP address: {ip}");
            return;
        }

        bool removed;
        lock (_lock)
        {
            removed = _bans.RemoveAll(b => b.IpAddress == ip) > 0;
        }

        Server.ExecuteCommand($"removeip {ip}");
        WriteBansToFile();

        ReplyToAdmin(player, removed
            ? $"{ColorGreen}[IPBan]{ColorDefault} Removed ban for {ip}."
            : $"{ColorDefault}[IPBan] No ban found for {ip}.");
    }

    // ═══════════════════════════════════════════════════
    //  Commands – listip
    // ═══════════════════════════════════════════════════

    [ConsoleCommand("css_listip", "List all banned IP addresses.")]
    [RequiresPermissions("@css/ban")]
    public void OnListIpCommand(CCSPlayerController? player, CommandInfo command)
    {
        PurgeExpired();

        List<IPBanEntry> snapshot;
        lock (_lock)
        {
            snapshot = new List<IPBanEntry>(_bans);
        }

        if (snapshot.Count == 0)
        {
            ReplyToAdmin(player, $"{ColorDefault}[IPBan] No IP bans active.");
            return;
        }

        ReplyToAdmin(player, $"{ColorGreen}[IPBan]{ColorDefault} --- Banned IPs ({snapshot.Count}) ---");
        foreach (var ban in snapshot)
        {
            string duration = ban.Minutes == 0 ? "permanent" : $"{FormatMinutes(ban.Minutes)}";
            string remaining = ban.Minutes == 0
                ? ""
                : $" expires {ban.BannedAt.AddMinutes(ban.Minutes):u}";
            string who = string.IsNullOrEmpty(ban.BannedBy) ? "Unknown" : ban.BannedBy;
            string dateStr = ban.BannedAt.ToString("u");
            string reasonStr = string.IsNullOrEmpty(ban.Reason) ? "No reason" : ban.Reason;
            ReplyToAdmin(player, $" {ColorDefault} {who} [{ban.IpAddress}] {dateStr} - {reasonStr}  ({duration}{remaining})");
        }
    }

    // ═══════════════════════════════════════════════════
    //  Commands – writeip
    // ═══════════════════════════════════════════════════

    [ConsoleCommand("css_writeip", "Write the current ban list to banned_ip.cfg.")]
    [RequiresPermissions("@css/ban")]
    public void OnWriteIpCommand(CCSPlayerController? player, CommandInfo command)
    {
        PurgeExpired();
        WriteBansToFile();
        ReplyToAdmin(player, $"{ColorGreen}[IPBan]{ColorDefault} Ban list written to banned_ip.cfg.");
    }

    // ═══════════════════════════════════════════════════
    //  Commands – help
    // ═══════════════════════════════════════════════════

    [ConsoleCommand("css_ipbanhelp", "Show all IPBan commands.")]
    [RequiresPermissions("@css/ban")]
    public void OnIpBanHelpCommand(CCSPlayerController? player, CommandInfo command) => PrintHelp(player);

    private void PrintHelp(CCSPlayerController? player)
    {
        ReplyToAdmin(player, $" {ColorDefault}---{ColorGreen} IPBan Commands {ColorDefault}---");
        ReplyToAdmin(player, $" {ColorGreen}addip <time> <ip> [\"reason\"]{ColorDefault} - Ban an IP (or no args for player menu)");
        ReplyToAdmin(player, $" {ColorGreen}banip / ipban{ColorDefault} - Aliases for addip");
        ReplyToAdmin(player, $" {ColorGreen}removeip <ip>{ColorDefault} - Unban an IP address");
        ReplyToAdmin(player, $" {ColorGreen}listip{ColorDefault} - List all active IP bans");
        ReplyToAdmin(player, $" {ColorGreen}writeip{ColorDefault} - Save ban list to banned_ip.cfg");
        ReplyToAdmin(player, $" {ColorGreen}ipbanhelp{ColorDefault} - Show this help message");
        ReplyToAdmin(player, $" {ColorDefault}Time examples: {ColorGreen}0{ColorDefault} (permanent), {ColorGreen}60{ColorDefault} (60 min), {ColorGreen}1 day{ColorDefault}, {ColorGreen}2 weeks{ColorDefault}, {ColorGreen}6 months{ColorDefault}");
    }

    // ═══════════════════════════════════════════════════
    //  Interactive Menu (CS2SimpleVote-style chat menu)
    // ═══════════════════════════════════════════════════

    private void ShowPlayerMenu(CCSPlayerController admin)
    {
        var playerList = new List<(string Name, string Ip)>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV)
                continue;
            string? rawIp = p.IpAddress;
            if (string.IsNullOrEmpty(rawIp))
                continue;
            string ip = rawIp.Split(':')[0];
            playerList.Add((p.PlayerName, ip));
        }

        if (playerList.Count == 0)
        {
            admin.PrintToChat($" {ColorDefault}[IPBan] No players online to ban.");
            return;
        }

        var state = new BanMenuState { Players = playerList };
        _menuStates[admin.Slot] = state;

        admin.PrintToChat($" {ColorGreen}[IPBan]{ColorDefault} Select a player to ban (or type 'cancel'):");
        for (int i = 0; i < playerList.Count; i++)
        {
            admin.PrintToChat($" {ColorGreen}[{i + 1}] {ColorDefault}{playerList[i].Name} - {playerList[i].Ip}");
        }
    }

    private void ShowTimePrompt(CCSPlayerController admin)
    {
        var state = _menuStates[admin.Slot];
        admin.PrintToChat($" {ColorGreen}[IPBan]{ColorDefault} Banning: {state.SelectedName} ({state.SelectedIp})");
        admin.PrintToChat($" {ColorDefault}Enter ban duration (or type 'cancel'):");
        admin.PrintToChat($" {ColorDefault}Examples: {ColorGreen}0{ColorDefault} (permanent), {ColorGreen}60{ColorDefault} (60 min), {ColorGreen}1 day{ColorDefault}, {ColorGreen}2 weeks{ColorDefault}, {ColorGreen}6 months{ColorDefault}");
    }

    private HookResult HandleMenuInput(CCSPlayerController player, string input)
    {
        if (!_menuStates.TryGetValue(player.Slot, out var state))
            return HookResult.Continue;

        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _menuStates.Remove(player.Slot);
            player.PrintToChat($" {ColorDefault}[IPBan] Cancelled.");
            return HookResult.Handled;
        }

        // Phase 1: Player selection
        if (!state.AwaitingTime)
        {
            if (int.TryParse(input, out int selection) && selection >= 1 && selection <= state.Players.Count)
            {
                var chosen = state.Players[selection - 1];
                state.SelectedIp = chosen.Ip;
                state.SelectedName = chosen.Name;
                state.AwaitingTime = true;
                ShowTimePrompt(player);
                return HookResult.Handled;
            }

            player.PrintToChat($" {ColorDefault}[IPBan] Invalid selection. Type a number or 'cancel'.");
            return HookResult.Handled;
        }

        // Phase 2: Time input
        if (state.AwaitingTime && !state.AwaitingVpnConfirm)
        {
            int minutes = ParseTimeToMinutes(input);
            if (minutes < 0)
            {
                player.PrintToChat($" {ColorDefault}[IPBan] Invalid time. Examples: 0, 60, 1 day, 2 weeks, 6 months");
                return HookResult.Handled;
            }

            string ip = state.SelectedIp!;
            state.PendingMinutes = minutes;

            // Async VPN check
            int menuSlot = player.Slot;
            _ = Task.Run(async () =>
            {
                if (_unloaded) return;
                var (vpnTag, _) = await CheckVpnAsync(ip);
                bool isVpn = vpnTag != "[NO VPN]";

                Server.NextFrame(() =>
                {
                    if (_unloaded) return;
                    var admin = Utilities.GetPlayerFromSlot(menuSlot);
                    if (admin == null || !admin.IsValid)
                    {
                        _menuStates.Remove(menuSlot);
                        return;
                    }
                    if (isVpn && _menuStates.ContainsKey(menuSlot))
                    {
                        state.AwaitingVpnConfirm = true;
                        admin.PrintToChat($" {ColorGreen}[IPBan]{ColorDefault} WARNING: {ip} is detected as {vpnTag}.");
                        admin.PrintToChat($" {ColorDefault}Banning a VPN IP may be ineffective. Proceed? Type {ColorGreen}yes{ColorDefault} or {ColorGreen}no{ColorDefault}.");
                    }
                    else
                    {
                        _menuStates.Remove(menuSlot);
                        ExecuteBan(admin, ip, minutes, state.PendingReason, admin.PlayerName);
                    }
                });
            });

            return HookResult.Handled;
        }

        // Phase 3: VPN confirmation (interactive menu path)
        if (state.AwaitingVpnConfirm)
        {
            if (input.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                string ip = state.SelectedIp!;
                int minutes = state.PendingMinutes;
                _menuStates.Remove(player.Slot);
                Server.NextFrame(() => ExecuteBan(player, ip, minutes, state.PendingReason, player.PlayerName));
                return HookResult.Handled;
            }

            if (input.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                _menuStates.Remove(player.Slot);
                player.PrintToChat($" {ColorDefault}[IPBan] Ban cancelled.");
                return HookResult.Handled;
            }

            player.PrintToChat($" {ColorDefault}[IPBan] Type {ColorGreen}yes{ColorDefault} or {ColorGreen}no{ColorDefault}.");
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    // ═══════════════════════════════════════════════════
    //  Chat listener (CS2SimpleVote-style)
    // ═══════════════════════════════════════════════════

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        if (_unloaded || player == null || !player.IsValid)
            return HookResult.Continue;

        if (!AdminManager.PlayerHasPermissions(player, "@css/ban"))
        {
            _menuStates.Remove(player.Slot);
            _vpnConfirmStates.Remove(player.Slot);
            return HookResult.Continue;
        }

        string msg = info.GetArg(1).Trim();
        string cleanMsg = msg.StartsWith('!') || msg.StartsWith('/') ? msg[1..] : msg;

        // Check direct-ban VPN confirmation first
        if (_vpnConfirmStates.TryGetValue(player.Slot, out var vpnState))
        {
            if (cleanMsg.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                string ip = vpnState.Ip;
                int minutes = vpnState.Minutes;
                string reason = vpnState.Reason;
                _vpnConfirmStates.Remove(player.Slot);
                Server.NextFrame(() => ExecuteBan(player, ip, minutes, reason, player.PlayerName));
                return HookResult.Handled;
            }

            if (cleanMsg.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                _vpnConfirmStates.Remove(player.Slot);
                player.PrintToChat($" {ColorDefault}[IPBan] Ban cancelled.");
                return HookResult.Handled;
            }

            player.PrintToChat($" {ColorDefault}[IPBan] Type {ColorGreen}yes{ColorDefault} or {ColorGreen}no{ColorDefault}.");
            return HookResult.Handled;
        }

        // Check interactive menu state
        if (_menuStates.ContainsKey(player.Slot))
            return HandleMenuInput(player, cleanMsg);

        // Route admin chat commands (with or without ! / prefix)
        return DispatchChatCommand(player, cleanMsg);
    }

    /// <summary>Parse and dispatch admin commands typed in chat. Accepts with or without ! prefix.</summary>
    private HookResult DispatchChatCommand(CCSPlayerController player, string text)
    {
        // Split into command + rest, respecting that the command is always the first word
        int spaceIdx = text.IndexOf(' ');
        string cmd = spaceIdx < 0 ? text : text[..spaceIdx];
        string args = spaceIdx < 0 ? "" : text[(spaceIdx + 1)..].Trim();

        switch (cmd.ToLowerInvariant())
        {
            case "addip" or "banip" or "ipban":
                Server.NextFrame(() => HandleChatBan(player, args));
                return HookResult.Handled;

            case "removeip":
                Server.NextFrame(() =>
                {
                    string ip = args.Trim();
                    if (string.IsNullOrEmpty(ip) || !IsValidIpAddress(ip))
                    {
                        player.PrintToChat($" {ColorDefault}[IPBan] Usage: removeip <ip>");
                        return;
                    }
                    bool removed;
                    lock (_lock) { removed = _bans.RemoveAll(b => b.IpAddress == ip) > 0; }
                    Server.ExecuteCommand($"removeip {ip}");
                    WriteBansToFile();
                    ReplyToAdmin(player, removed
                        ? $"{ColorGreen}[IPBan]{ColorDefault} Removed ban for {ip}."
                        : $"{ColorDefault}[IPBan] No ban found for {ip}.");
                });
                return HookResult.Handled;

            case "listip":
                Server.NextFrame(() => OnListIpCommand(player, null!));
                return HookResult.Handled;

            case "writeip":
                Server.NextFrame(() => OnWriteIpCommand(player, null!));
                return HookResult.Handled;

            case "ipbanhelp" or "help":
                Server.NextFrame(() => PrintHelp(player));
                return HookResult.Continue;

            default:
                return HookResult.Continue;
        }
    }

    /// <summary>Handle addip/banip/ipban from raw chat text, supporting quoted reasons.</summary>
    private void HandleChatBan(CCSPlayerController player, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            ShowPlayerMenu(player);
            return;
        }

        // Tokenize respecting quoted strings
        var tokens = TokenizeChatArgs(args);

        // Find the IP token
        string ip = "";
        int ipTokenIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (IsValidIpAddress(tokens[i]))
            {
                ip = tokens[i];
                ipTokenIndex = i;
                break;
            }
        }

        if (ipTokenIndex < 0)
        {
            player.PrintToChat($" {ColorDefault}[IPBan] Usage: addip <time> <ip> [\"reason\"]");
            return;
        }

        // Time is everything before the IP
        string timeStr = string.Join(" ", tokens.Take(ipTokenIndex));
        int minutes = ParseTimeToMinutes(timeStr);
        if (minutes < 0)
        {
            player.PrintToChat($" {ColorDefault}[IPBan] Invalid time: {timeStr}. Examples: 0, 60, 1 day, 2 weeks, 6 months");
            return;
        }

        // Reason is everything after the IP (already unquoted by tokenizer)
        string reason = string.Join(" ", tokens.Skip(ipTokenIndex + 1));
        string adminName = player.PlayerName;

        // Async VPN check before banning
        int chatBanSlot = player.Slot;
        _ = Task.Run(async () =>
        {
            if (_unloaded) return;
            var (vpnTag, _) = await CheckVpnAsync(ip);
            bool isVpn = vpnTag != "[NO VPN]";

            Server.NextFrame(() =>
            {
                if (_unloaded) return;
                var admin = Utilities.GetPlayerFromSlot(chatBanSlot);
                if (admin == null || !admin.IsValid) return;
                if (isVpn)
                {
                    _vpnConfirmStates[admin.Slot] = new VpnConfirmState { Ip = ip, Minutes = minutes, Reason = reason };
                    admin.PrintToChat($" {ColorGreen}[IPBan]{ColorDefault} WARNING: {ip} is detected as {vpnTag}.");
                    admin.PrintToChat($" {ColorDefault}Banning a VPN IP may be ineffective. Proceed? Type {ColorGreen}yes{ColorDefault} or {ColorGreen}no{ColorDefault}.");
                }
                else
                {
                    ExecuteBan(admin, ip, minutes, reason, adminName);
                }
            });
        });
    }

    /// <summary>Split a string into tokens, treating quoted sections as single tokens and stripping the quotes.</summary>
    private static List<string> TokenizeChatArgs(string input)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < input.Length)
        {
            // Skip whitespace
            while (i < input.Length && input[i] == ' ') i++;
            if (i >= input.Length) break;

            if (input[i] == '"')
            {
                // Quoted token — find closing quote
                i++; // skip opening quote
                int start = i;
                while (i < input.Length && input[i] != '"') i++;
                tokens.Add(input[start..i]);
                if (i < input.Length) i++; // skip closing quote
            }
            else
            {
                // Unquoted token
                int start = i;
                while (i < input.Length && input[i] != ' ') i++;
                tokens.Add(input[start..i]);
            }
        }
        return tokens;
    }

    // ═══════════════════════════════════════════════════
    //  Client connect – ban enforcement & admin notify
    // ═══════════════════════════════════════════════════

    private void OnClientConnected(int playerSlot)
    {
        if (_unloaded) return;

        Server.NextFrame(() =>
        {
            if (_unloaded) return;

            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                return;

            string? rawIp = player.IpAddress;
            if (string.IsNullOrEmpty(rawIp))
                return;

            string ip = rawIp.Split(':')[0];

            PurgeExpired();

            bool isBanned;
            lock (_lock)
            {
                isBanned = _bans.Any(b => b.IpAddress == ip);
            }

            string playerName = player.PlayerName;
            ulong steamId = player.SteamID;

            if (isBanned)
            {
                Server.ExecuteCommand($"kickid {player.UserId} \"Your IP address is banned from this server.\"");
                NotifyAdmins($" {ColorRed}[IPBan] BLOCKED: {playerName} | {steamId} | {ip} (IP is banned){ColorDefault}");
                LogBlockedAttempt(ip, steamId, playerName);
                return;
            }

            _ = Task.Run(async () =>
            {
                if (_unloaded) return;
                var (vpnTag, geoLocation) = await CheckVpnAsync(ip);
                bool isVpn = vpnTag != "[NO VPN]";

                // Record IP ↔ SteamID association (skip VPN IPs)
                if (!isVpn)
                    RecordAccountAssociation(ip, steamId, playerName);

                // Find associated accounts (through shared non-VPN IPs)
                var associated = GetAssociatedAccounts(steamId);

                Server.NextFrame(() =>
                {
                    if (_unloaded) return;
                    string vpnColor = isVpn ? ColorRed : ColorYellow;
                    string vpnLabel = isVpn ? vpnTag : "[No VPN]";
                    string locPart = !string.IsNullOrEmpty(geoLocation) ? $" | {geoLocation}" : "";
                    string connectMsg = $" {ColorGreen}{playerName}{ColorDefault} | {steamId} | {ip}{locPart} | {vpnColor}{vpnLabel}{ColorDefault}";

                    if (isVpn)
                        NotifyAdmins(connectMsg);
                    else
                        NotifyAdminsConsole(connectMsg);

                    if (associated.Count > 0)
                    {
                        var coloredNames = associated.Select(n => $"{ColorBlue}{n}{ColorDefault}");
                        string nameList = string.Join($"{ColorDefault}, ", coloredNames);
                        if (isVpn)
                            NotifyAdmins($" [{nameList}]");
                        else
                            NotifyAdminsConsole($" [{nameList}]");
                    }
                });
            });
        });
    }

    private void OnClientDisconnected(int playerSlot)
    {
        _menuStates.Remove(playerSlot);
        _vpnConfirmStates.Remove(playerSlot);
    }

    // ═══════════════════════════════════════════════════
    //  IP ↔ Account Association Database
    // ═══════════════════════════════════════════════════

    private void RecordAccountAssociation(string ip, ulong steamId, string currentName)
    {
        lock (_dbLock)
        {
            if (!_accountDb.IpToAccounts.TryGetValue(ip, out var accounts))
            {
                accounts = new List<AccountRecord>();
                _accountDb.IpToAccounts[ip] = accounts;
            }

            var existing = accounts.FirstOrDefault(a => a.SteamId == steamId);
            if (existing != null)
            {
                // Update name in case it changed
                existing.Name = currentName;
            }
            else
            {
                accounts.Add(new AccountRecord { SteamId = steamId, Name = currentName });
            }
        }

        SaveAccountDatabase();
    }

    /// <summary>
    /// Find all accounts associated with a given SteamID.
    /// Association is transitive through shared non-VPN IPs:
    /// if A shared IP1 with B, and B shared IP2 with C, then A-B-C are all associated.
    /// Returns display names of associated accounts (excluding the queried one).
    /// </summary>
    private List<string> GetAssociatedAccounts(ulong steamId)
    {
        lock (_dbLock)
        {
            // BFS/flood-fill: find all SteamIDs reachable through shared IPs
            var visited = new HashSet<ulong> { steamId };
            var queue = new Queue<ulong>();
            queue.Enqueue(steamId);

            // Build a quick reverse map: SteamID → set of IPs
            var steamToIps = new Dictionary<ulong, List<string>>();
            foreach (var (ip, accounts) in _accountDb.IpToAccounts)
            {
                foreach (var acct in accounts)
                {
                    if (!steamToIps.TryGetValue(acct.SteamId, out var ips))
                    {
                        ips = new List<string>();
                        steamToIps[acct.SteamId] = ips;
                    }
                    ips.Add(ip);
                }
            }

            while (queue.Count > 0)
            {
                ulong current = queue.Dequeue();
                if (!steamToIps.TryGetValue(current, out var currentIps))
                    continue;

                foreach (string ip in currentIps)
                {
                    if (!_accountDb.IpToAccounts.TryGetValue(ip, out var accounts))
                        continue;

                    foreach (var acct in accounts)
                    {
                        if (visited.Add(acct.SteamId))
                            queue.Enqueue(acct.SteamId);
                    }
                }
            }

            // Collect names of all associated accounts except the queried one
            var result = new List<string>();
            foreach (var (_, accounts) in _accountDb.IpToAccounts)
            {
                foreach (var acct in accounts)
                {
                    if (acct.SteamId != steamId && visited.Contains(acct.SteamId))
                    {
                        if (!result.Contains(acct.Name))
                            result.Add(acct.Name);
                    }
                }
            }

            return result;
        }
    }

    private void LoadAccountDatabase()
    {
        lock (_dbLock)
        {
            _accountDb = new IpAccountDatabase();
        }

        if (!File.Exists(_accountDbPath))
            return;

        try
        {
            string json = File.ReadAllText(_accountDbPath);
            var db = JsonSerializer.Deserialize<IpAccountDatabase>(json);
            if (db != null)
            {
                lock (_dbLock)
                {
                    _accountDb = db;
                }
            }

            int totalAccounts = 0;
            lock (_dbLock)
            {
                foreach (var kv in _accountDb.IpToAccounts)
                    totalAccounts += kv.Value.Count;
            }
            Console.WriteLine($"[IPBan] Loaded account database: {_accountDb.IpToAccounts.Count} IPs, {totalAccounts} records.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IPBan] Failed to load account database: {ex.Message}");
        }
    }

    private void SaveAccountDatabase()
    {
        lock (_dbLock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_accountDbPath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(_accountDb, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_accountDbPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IPBan] Failed to save account database: {ex.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  VPN / Datacenter Detection
    // ═══════════════════════════════════════════════════

    private async Task<(string VpnTag, string Location)> CheckVpnAsync(string ip)
    {
        if (IsDatacenterIp(ip))
            return ("[VPN/DATACENTER]", "");

        try
        {
            var client = _httpClient;
            if (client == null) return ("[NO VPN]", "");
            string url = $"http://ip-api.com/json/{Uri.EscapeDataString(ip)}?fields=status,city,regionName,countryCode,isp,org,proxy,hosting";
            string json = await client.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusProp) &&
                statusProp.GetString() == "success")
            {
                bool isProxy = root.TryGetProperty("proxy", out var proxyProp) && proxyProp.GetBoolean();
                bool isHosting = root.TryGetProperty("hosting", out var hostingProp) && hostingProp.GetBoolean();

                string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                string region = root.TryGetProperty("regionName", out var r) ? r.GetString() ?? "" : "";
                string country = root.TryGetProperty("countryCode", out var cc) ? cc.GetString() ?? "" : "";
                string isp = root.TryGetProperty("isp", out var ispProp) ? ispProp.GetString() ?? "" : "";
                string org = root.TryGetProperty("org", out var orgProp) ? orgProp.GetString() ?? "" : "";

                // Build compact location: "City, ST, US"
                var locParts = new List<string>();
                if (!string.IsNullOrEmpty(city)) locParts.Add(city);
                if (!string.IsNullOrEmpty(region)) locParts.Add(region);
                if (!string.IsNullOrEmpty(country)) locParts.Add(country);
                string location = string.Join(", ", locParts);

                if (isProxy)
                {
                    string provider = !string.IsNullOrEmpty(org) ? org : isp;
                    string vpnDetail = !string.IsNullOrEmpty(provider) ? $"[VPN/PROXY: {provider}]" : "[VPN/PROXY]";
                    return (vpnDetail, location);
                }
                if (isHosting)
                {
                    string provider = !string.IsNullOrEmpty(org) ? org : isp;
                    string vpnDetail = !string.IsNullOrEmpty(provider) ? $"[DC: {provider}]" : "[VPN/DATACENTER]";
                    return (vpnDetail, location);
                }

                return ("[NO VPN]", location);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IPBan] VPN lookup failed for {ip}: {ex.Message}");
        }

        return ("[NO VPN]", "");
    }

    private static bool IsDatacenterIp(string ipStr)
    {
        if (!IPAddress.TryParse(ipStr, out var addr) ||
            addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        byte[] bytes = addr.GetAddressBytes();
        uint ipNum = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];

        foreach (var (network, mask) in DatacenterCidrs)
        {
            if ((ipNum & mask) == network)
                return true;
        }

        return false;
    }

    private static (uint Network, uint Mask)[] BuildCidrTable(string[] cidrs)
    {
        var result = new (uint, uint)[cidrs.Length];
        for (int i = 0; i < cidrs.Length; i++)
        {
            var parts = cidrs[i].Split('/');
            var addr = IPAddress.Parse(parts[0]);
            int prefix = int.Parse(parts[1]);
            byte[] bytes = addr.GetAddressBytes();
            uint ip = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
            uint mask = prefix == 0 ? 0 : 0xFFFFFFFF << (32 - prefix);
            result[i] = (ip & mask, mask);
        }
        return result;
    }

    // ═══════════════════════════════════════════════════
    //  Admin-only messaging
    // ═══════════════════════════════════════════════════

    /// <summary>Send a message only to the given admin (or console). Non-admins never see output.</summary>
    private static void ReplyToAdmin(CCSPlayerController? player, string message)
    {
        if (player == null)
        {
            Console.WriteLine(message);
            return;
        }

        if (AdminManager.PlayerHasPermissions(player, "@css/ban"))
            player.PrintToChat(message);
    }

    /// <summary>Broadcast a message to all online admins and server console.</summary>
    private static void NotifyAdmins(string message)
    {
        Console.WriteLine(message);

        foreach (var admin in Utilities.GetPlayers())
        {
            if (admin == null || !admin.IsValid || admin.IsBot || admin.IsHLTV)
                continue;

            if (AdminManager.PlayerHasPermissions(admin, "@css/ban"))
                admin.PrintToChat(message);
        }
    }

    /// <summary>Send a message to admin consoles only (PrintToConsole), not chat.</summary>
    private static void NotifyAdminsConsole(string message)
    {
        Console.WriteLine(message);

        foreach (var admin in Utilities.GetPlayers())
        {
            if (admin == null || !admin.IsValid || admin.IsBot || admin.IsHLTV)
                continue;

            if (AdminManager.PlayerHasPermissions(admin, "@css/ban"))
                admin.PrintToConsole(message);
        }
    }

    // ═══════════════════════════════════════════════════
    //  Blocked connection logging
    // ═══════════════════════════════════════════════════

    private void LogBlockedAttempt(string ip, ulong steamId, string playerName)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_blockedLogPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var entry = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                ip,
                steamId = steamId.ToString(),
                playerName
            };
            string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            File.AppendAllText(_blockedLogPath, line);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IPBan] Failed to log blocked attempt: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════
    //  Time parsing
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Parse flexible time strings into minutes.
    /// Accepts: plain number (minutes), or "N unit" forms.
    /// Units: minute(s)/min/m, hour(s)/hr/h, day(s)/d, week(s)/w, month(s)/mo, year(s)/y
    /// Returns -1 on parse failure.
    /// </summary>
    private static int ParseTimeToMinutes(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input))
            return -1;

        // Plain number → minutes
        if (int.TryParse(input, out int plainMinutes) && plainMinutes >= 0)
            return plainMinutes;

        // Try "N unit" pattern, supporting multiple tokens like "1 day 2 hours"
        var matches = TimeTokenRegex().Matches(input);
        if (matches.Count == 0)
            return -1;

        long totalMinutes = 0;
        foreach (Match m in matches)
        {
            if (!long.TryParse(m.Groups[1].Value, out long value) || value < 0)
                return -1;

            string unit = m.Groups[2].Value.ToLowerInvariant().TrimEnd('s');
            long multiplier = unit switch
            {
                "minute" or "min" or "m" => 1,
                "hour" or "hr" or "h" => 60,
                "day" or "d" => 1440,
                "week" or "w" => 10080,
                "month" or "mo" => 43200, // 30 days
                "year" or "yr" or "y" => 525600, // 365 days
                _ => -1
            };

            if (multiplier < 0) return -1;
            totalMinutes += value * multiplier;
        }

        if (totalMinutes > int.MaxValue) return int.MaxValue;
        return (int)totalMinutes;
    }

    /// <summary>Format minutes back into a readable string.</summary>
    private static string FormatMinutes(int minutes)
    {
        if (minutes == 0) return "permanent";
        if (minutes < 60) return $"{minutes} minute(s)";
        if (minutes < 1440) return $"{minutes / 60} hour(s) {minutes % 60} min";
        if (minutes < 10080) return $"{minutes / 1440} day(s)";
        if (minutes < 43200) return $"{minutes / 10080} week(s)";
        return $"{minutes / 43200} month(s)";
    }

    // ═══════════════════════════════════════════════════
    //  File I/O
    // ═══════════════════════════════════════════════════

    private void LoadBansFromFile()
    {
        if (!File.Exists(_bannedIpCfgPath))
        {
            lock (_lock) { _bans.Clear(); }
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(_bannedIpCfgPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IPBan] Failed to read {_bannedIpCfgPath}: {ex.Message}");
            return;
        }

        var regex = AddIpLineRegex();
        var loaded = new List<IPBanEntry>();

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//"))
                continue;

            var match = regex.Match(trimmed);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups[1].Value, out int minutes))
                minutes = 0;

            string ip = match.Groups[2].Value;
            if (!IsValidIpAddress(ip))
                continue;

            if (loaded.Any(b => b.IpAddress == ip))
                continue;

            // Parse optional trailing comment: //bannedBy, utcDate, reason
            string bannedBy = "";
            DateTime bannedAt = DateTime.UtcNow;
            string reason = "";
            int commentIdx = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0)
            {
                string comment = trimmed[(commentIdx + 2)..];
                string[] parts = comment.Split(',', 3);
                if (parts.Length >= 1)
                    bannedBy = parts[0].Trim();
                if (parts.Length >= 2 && DateTime.TryParse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    bannedAt = parsed;
                if (parts.Length >= 3)
                    reason = parts[2].Trim();
            }

            loaded.Add(new IPBanEntry
            {
                IpAddress = ip,
                Minutes = minutes,
                BannedAt = bannedAt,
                BannedBy = bannedBy,
                Reason = reason
            });
        }

        lock (_lock)
        {
            _bans.Clear();
            _bans.AddRange(loaded);
        }

        Console.WriteLine($"[IPBan] Loaded {loaded.Count} ban(s) from banned_ip.cfg.");
    }

    private void WriteBansToFile()
    {
        PurgeExpired();

        List<IPBanEntry> snapshot;
        lock (_lock)
        {
            snapshot = new List<IPBanEntry>(_bans);
        }

        try
        {
            string? dir = Path.GetDirectoryName(_bannedIpCfgPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("// This file is managed by IPBanPlugin. Do not edit while server is running.");
            foreach (var ban in snapshot)
            {
                string safeName = ban.BannedBy.Replace(',', ' ');
                string comment = $"//{safeName}, {ban.BannedAt:u}, {ban.Reason}";
                sb.AppendLine($"addip {ban.Minutes} \"{ban.IpAddress}\" {comment}");
            }

            File.WriteAllText(_bannedIpCfgPath, sb.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IPBan] Failed to write {_bannedIpCfgPath}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════

    private void PurgeExpired()
    {
        lock (_lock)
        {
            _bans.RemoveAll(b => b.IsExpired);
        }
    }

    private void KickBannedPlayers()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                continue;

            string? rawIp = player.IpAddress;
            if (string.IsNullOrEmpty(rawIp))
                continue;

            string ip = rawIp.Split(':')[0];

            bool isBanned;
            lock (_lock)
            {
                isBanned = _bans.Any(b => b.IpAddress == ip);
            }

            if (isBanned)
            {
                Server.ExecuteCommand($"kickid {player.UserId} \"Your IP address is banned from this server.\"");
            }
        }
    }

    private static bool IsValidIpAddress(string ip)
    {
        return IPAddress.TryParse(ip, out var addr)
               && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    [GeneratedRegex(@"^addip\s+(\d+)\s+""?(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})""?", RegexOptions.IgnoreCase)]
    private static partial Regex AddIpLineRegex();

    [GeneratedRegex(@"(\d+)\s*(minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|w|months?|mo|years?|yrs?|y)", RegexOptions.IgnoreCase)]
    private static partial Regex TimeTokenRegex();
}
