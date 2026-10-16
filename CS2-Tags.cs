using ChatManagerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PlayerSettings;

namespace CS2_Tags;

/// <summary>
/// CS2-Tags: a selectable-tag provider for the shared ChatManager. Tags live in commented .jsonc
/// files under configs/ (admin_tags.jsonc for #group/@permission, custom_tags.jsonc for per-SteamID
/// lists), with an optional MySQL store that syncs custom tags both ways (most-recent-wins).
///
/// A player has NO tag by default. !tags opens a center-HTML menu listing every tag they qualify
/// for (Disable first); the choice is persisted via the PlayerSettings plugin when present.
/// All chat rendering / scoreboard writing / gag handling lives in ChatManager.
/// </summary>
[MinimumApiVersion(374)]
public class CS2_Tags : BasePlugin
{
    public override string ModuleName => "CS2-Tags";
    public override string ModuleDescription => "Selectable player tags with MySQL sync";
    public override string ModuleAuthor => " daffy & panda";
    public override string ModuleVersion => "1.0.1";

    private const int TagOrder = 20;          // after VIP (10)
    private const int TagColorPriority = 100; // admin wins colours over VIP (50)
    private const string ProviderName = "cs2tags";
    private const string SettingKey = "cs2tags";
    private const string Disabled = "@disabled";

    private readonly PluginCapability<IChatManagerApi?> _chatManagerCap = IChatManagerApi.Capability;
    private readonly PluginCapability<ISettingsApi?> _settingsCap = new("settings:nfcore");

    private IChatManagerApi? _chatManager;
    private ISettingsApi? _settings;

    private TagConfig _config = new();
    private TagStore _store = null!;
    private readonly HttpClient _http = new();
    private readonly string[] _memoryChoice = new string[70];
    private CounterStrikeSharp.API.Modules.Timers.Timer? _syncTimer;

    public override void Load(bool hotReload)
    {
        _config = LoadConfig();
        _store = new TagStore(Logger, Path.Combine(ModuleDirectory, "configs"), _config, _http);
        _store.Initialize();

        // DB mode: periodic two-way sync + Steam persona name resolution for the comments.
        if (_config.UseDatabase)
        {
            var interval = Math.Max(10, _config.SyncIntervalSeconds);
            _syncTimer = AddTimer(interval, SyncTick, TimerFlags.REPEAT);
            Server.NextFrame(SyncTick); // also run once shortly after load
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _chatManager = _chatManagerCap.Get();
        if (_chatManager == null)
            Logger.LogError("[CS2-Tags] ChatManager not found - install the ChatManager plugin + shared ChatManagerApi.dll.");
        else
            _chatManager.RegisterProvider(ProviderName, GetTag);

        try { _settings = _settingsCap.Get(); } catch { _settings = null; }
        if (_settings == null)
            Logger.LogInformation("[CS2-Tags] PlayerSettings not found - tag choices are kept per session only.");
    }

    public override void Unload(bool hotReload)
    {
        _syncTimer?.Kill();
        _chatManager?.UnregisterProvider(ProviderName);
        _http.Dispose();
    }

    private void SyncTick()
    {
        // Resolve Steam names first (so the comments are present when the file is written), then sync.
        Task.Run(async () =>
        {
            try
            {
                await _store.ResolvePersonaNamesAsync(_store.CustomSteamIds());
            }
            catch { /* best effort */ }

            Server.NextFrame(() =>
            {
                _store.SyncOnce();
                // Re-apply tags for everyone online so DB changes show without waiting for a chat line.
                foreach (var p in Utilities.GetPlayers())
                    if (p is { IsValid: true, IsBot: false })
                        _chatManager?.RefreshTags(p);
            });
        });
    }

    [ConsoleCommand("css_tags_reload", "Reloads the tag config files (and syncs in db mode).")]
    public void OnReloadConfig(CCSPlayerController? player, CommandInfo info)
    {
        if (player != null) return;
        _config = LoadConfig();
        _store.ReloadFromFiles();
        if (_config.UseDatabase) _store.SyncOnce();
        Server.PrintToConsole(Localizer["console.Reloaded", Localizer["Prefix"]]);
    }

    [ConsoleCommand("css_tags", "Opens the tag selection menu.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnTagsCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player is { IsValid: true, IsBot: false })
            OpenTagsMenu(player);
    }

    // --- Menu ---------------------------------------------------------------------------------

    private void OpenTagsMenu(CCSPlayerController player)
    {
        var menu = new CenterHtmlMenu(Localizer["menu.Title"], this);
        var current = GetChoice(player);

        var disableLabel = Localizer["menu.Disable"];
        if (current == Disabled) disableLabel = Localizer["menu.Selected", disableLabel];
        menu.AddMenuOption(disableLabel, (p, _) =>
        {
            SetChoice(p, Disabled);
            MenuManager.CloseActiveMenu(p);
            p.PrintToChat(Localizer["tag.Disabled", Localizer["Prefix"]]);
        });

        foreach (var def in GetAvailableTags(player))
        {
            var label = current.Equals(def.Id, StringComparison.OrdinalIgnoreCase)
                ? Localizer["menu.Selected", def.Name]
                : def.Name;
            var capturedId = def.Id;
            var capturedName = def.Name;
            menu.AddMenuOption(label, (p, _) =>
            {
                SetChoice(p, capturedId);
                MenuManager.CloseActiveMenu(p);
                p.PrintToChat(Localizer["tag.Set", Localizer["Prefix"], capturedName]);
            });
        }

        MenuManager.OpenCenterHtmlMenu(this, player, menu);
    }

    // --- ChatManager provider -----------------------------------------------------------------

    private ChatTag? GetTag(CCSPlayerController player)
    {
        if (player is not { IsValid: true } || player.IsBot || player.IsHLTV)
            return null;

        var choice = GetChoice(player);
        if (choice == Disabled)
            return null;

        var def = _store.GetById(choice);
        if (def == null || !PlayerQualifies(player, def))
            return null;

        return new ChatTag
        {
            ChatPrefix = ApplyVariables(def.Prefix, player),
            ScoreboardTag = ApplyVariables(def.Scoreboard, player),
            NameColor = StripBraces(def.NickColor),
            MessageColor = StripBraces(def.MessageColor),
            Order = TagOrder,
            ColorPriority = TagColorPriority
        };
    }

    /// <summary>
    /// Replaces per-player variables inside a tag field. Currently supports {userid} (the player's
    /// in-game user id), like CS2UserIDTags. Case-insensitive.
    /// </summary>
    private static string ApplyVariables(string text, CCSPlayerController player)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("{userid}", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        var userId = player.UserId?.ToString() ?? "0";
        return System.Text.RegularExpressions.Regex.Replace(
            text, "\\{userid\\}", userId, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // --- Availability -------------------------------------------------------------------------

    private List<TagDef> GetAvailableTags(CCSPlayerController player)
        => _store.AllTags().Where(def => PlayerQualifies(player, def)).ToList();

    private static bool PlayerQualifies(CCSPlayerController player, TagDef def)
    {
        if (def.IsExpired) return false;
        var steamid = player.AuthorizedSteamID?.SteamId64.ToString() ?? player.SteamID.ToString();
        return def.Kind switch
        {
            TagKind.Everyone => true,
            TagKind.SteamId => def.Owner == steamid,
            TagKind.Group => AdminManager.PlayerInGroup(player, def.Owner),
            TagKind.Permission => AdminManager.PlayerHasPermissions(player, def.Owner),
            _ => false
        };
    }

    // --- Choice storage -----------------------------------------------------------------------

    private string GetChoice(CCSPlayerController player)
    {
        if (_settings != null)
            return _settings.GetPlayerSettingsValue(player, SettingKey, Disabled);

        var slot = player.Slot;
        if (slot >= 0 && slot < _memoryChoice.Length && !string.IsNullOrEmpty(_memoryChoice[slot]))
            return _memoryChoice[slot];
        return Disabled;
    }

    private void SetChoice(CCSPlayerController player, string value)
    {
        if (_settings != null)
            _settings.SetPlayerSettingsValue(player, SettingKey, value);
        else
        {
            var slot = player.Slot;
            if (slot >= 0 && slot < _memoryChoice.Length)
                _memoryChoice[slot] = value;
        }

        _chatManager?.RefreshTags(player);
    }

    // --- Config -------------------------------------------------------------------------------

    private TagConfig LoadConfig()
    {
        var dir = Path.Combine(ModuleDirectory, "configs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cs2-tags.jsonc");

        if (!File.Exists(path))
        {
            var def = new TagConfig();
            File.WriteAllText(path, BuildConfigJsonc(def));
            return def;
        }

        try
        {
            // Strip comments by letting Newtonsoft read them, then deserialize.
            using var reader = new JsonTextReader(new StreamReader(path));
            var token = Newtonsoft.Json.Linq.JToken.ReadFrom(reader);
            return token.ToObject<TagConfig>() ?? new TagConfig();
        }
        catch (Exception ex)
        {
            Logger.LogError("[CS2-Tags] Failed to parse cs2-tags.jsonc: {Err} - using defaults.", ex.Message);
            return new TagConfig();
        }
    }

    private static string BuildConfigJsonc(TagConfig c)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // Storage: \"local\" = tags only from the .jsonc files.");
        sb.AppendLine("  //          \"db\"    = MySQL store; custom tags are synced both ways with");
        sb.AppendLine("  //                   custom_tags.jsonc every SyncIntervalSeconds (most-recent-wins).");
        sb.AppendLine($"  \"Storage\": {JsonConvert.ToString(c.Storage)},");
        sb.AppendLine($"  \"SyncIntervalSeconds\": {c.SyncIntervalSeconds},");
        sb.AppendLine("  // Steam Web API key: used to write the player's name as a // comment above each");
        sb.AppendLine("  // SteamID block in custom_tags.jsonc (works for offline players). Optional.");
        sb.AppendLine($"  \"SteamApiKey\": {JsonConvert.ToString(c.SteamApiKey)},");
        sb.AppendLine("  \"Mysql\": {");
        sb.AppendLine($"    \"Host\": {JsonConvert.ToString(c.Mysql.Host)},");
        sb.AppendLine($"    \"Port\": {c.Mysql.Port},");
        sb.AppendLine($"    \"User\": {JsonConvert.ToString(c.Mysql.User)},");
        sb.AppendLine($"    \"Password\": {JsonConvert.ToString(c.Mysql.Password)},");
        sb.AppendLine($"    \"Database\": {JsonConvert.ToString(c.Mysql.Database)},");
        sb.AppendLine($"    \"Table\": {JsonConvert.ToString(c.Mysql.Table)},");
        sb.AppendLine($"    \"TableAdmin\": {JsonConvert.ToString(c.Mysql.TableAdmin)}");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string StripBraces(string token)
    {
        var t = token.Trim();
        return t.Length >= 2 && t.StartsWith('{') && t.EndsWith('}') ? t[1..^1] : t;
    }
}
