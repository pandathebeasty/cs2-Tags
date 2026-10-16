using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CS2_Tags;

/// <summary>
/// Owns loading and persisting tag definitions:
///   * admin_tags.jsonc  - "#group" / "@permission" tags (single per key, JSON only)
///   * custom_tags.jsonc - per-SteamID64 LISTS of tags (multiple allowed), optionally DB-synced
///
/// In "db" mode the custom tags are synced both ways with MySQL on a timer, most-recent-wins by the
/// per-tag "updated" timestamp. When a Steam Web API key is configured, a "// personaname" comment
/// is written above each SteamID block in custom_tags.jsonc.
/// </summary>
public sealed class TagStore
{
    private readonly ILogger _logger;
    private readonly string _configDir;
    private readonly TagConfig _config;
    private readonly HttpClient _http;

    private readonly string _adminPath;
    private readonly string _customPath;

    // All currently active tags, keyed by TagDef.Id.
    private readonly Dictionary<string, TagDef> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // SteamID64 -> resolved persona name (for the comment above each block). Best-effort cache.
    private readonly Dictionary<string, string> _personaNames = new();

    // Signature of the last custom_tags.jsonc content we wrote, INCLUDING persona-name comments, so
    // the file is rewritten when a newly-resolved name would change it (not just when tags change).
    private string _lastCustomFileSig = string.Empty;

    public TagStore(ILogger logger, string configDir, TagConfig config, HttpClient http)
    {
        _logger = logger;
        _configDir = configDir;
        _config = config;
        _http = http;
        _adminPath = Path.Combine(configDir, "admin_tags.jsonc");
        _customPath = Path.Combine(configDir, "custom_tags.jsonc");
    }

    /// <summary>Snapshot of all non-expired tags (thread-safe copy).</summary>
    public List<TagDef> AllTags()
    {
        lock (_lock)
            return _tags.Values.Where(t => !t.IsExpired).ToList();
    }

    public TagDef? GetById(string id)
    {
        lock (_lock)
            return _tags.TryGetValue(id, out var t) && !t.IsExpired ? t : null;
    }

    // --- Loading ------------------------------------------------------------------------------

    public void Initialize()
    {
        Directory.CreateDirectory(_configDir);
        EnsureDefaultFiles();
        ReloadFromFiles();

        if (_config.UseDatabase)
        {
            try
            {
                EnsureTable();
                SyncOnce(); // prime from DB immediately
            }
            catch (Exception ex)
            {
                _logger.LogError("[CS2-Tags] MySQL init/sync failed: {Err}", ex.Message);
            }
        }
    }

    /// <summary>Reloads both JSON files into memory. Admin tags replace, custom tags replace.</summary>
    public void ReloadFromFiles()
    {
        var loaded = new Dictionary<string, TagDef>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in LoadAdminFile())
            loaded[def.Id] = def;

        foreach (var def in LoadCustomFile())
            loaded[def.Id] = def;

        lock (_lock)
        {
            _tags.Clear();
            foreach (var kv in loaded)
                _tags[kv.Key] = kv.Value;
        }
    }

    private List<TagDef> LoadAdminFile()
    {
        var result = new List<TagDef>();
        var root = ParseJsonc(_adminPath);
        if (root?["tags"] is not JObject tagsObj) return result;

        foreach (var prop in tagsObj.Properties())
        {
            if (prop.Value is not JObject entry) continue;

            var key = prop.Name;
            var kind = key.StartsWith('#') ? TagKind.Group
                     : key.StartsWith('@') ? TagKind.Permission
                     : string.Equals(key, "everyone", StringComparison.OrdinalIgnoreCase) ? TagKind.Everyone
                     : TagKind.SteamId;

            if (kind == TagKind.SteamId)
            {
                _logger.LogWarning("[CS2-Tags] '{Key}' in admin_tags.jsonc is not a #group/@permission/everyone - ignored.", key);
                continue;
            }

            // Same "updated: 0 (or missing) = apply this edit now" rule as custom tags, so a manual
            // JSON edit wins over the DB on the next sync.
            var adminUpdatedRaw = entry["updated"]?.Value<long>() ?? 0;
            var adminUpdated = adminUpdatedRaw <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : adminUpdatedRaw;

            result.Add(new TagDef
            {
                Id = key,
                Owner = key,
                Kind = kind,
                Name = entry["name"]?.ToString() ?? key,
                Prefix = entry["prefix"]?.ToString() ?? "",
                NickColor = entry["nick_color"]?.ToString() ?? "",
                MessageColor = entry["message_color"]?.ToString() ?? "",
                Scoreboard = entry["scoreboard"]?.ToString() ?? "",
                ExpiresUnix = 0,
                UpdatedUnix = adminUpdated
            });
        }

        return result;
    }

    private List<TagDef> LoadCustomFile()
    {
        var result = new List<TagDef>();
        var root = ParseJsonc(_customPath);
        if (root?["tags"] is not JObject tagsObj) return result;

        foreach (var prop in tagsObj.Properties())
        {
            var steamid = prop.Name;
            if (steamid.Length != 17 || !steamid.All(char.IsDigit))
            {
                _logger.LogWarning("[CS2-Tags] '{Key}' in custom_tags.jsonc is not a SteamID64 - ignored.", steamid);
                continue;
            }

            // Each SteamID maps to a LIST of tag objects (multiple tags per player).
            var entries = prop.Value as JArray ?? (prop.Value is JObject single ? [single] : new JArray());
            var index = 0;
            foreach (var token in entries)
            {
                if (token is not JObject entry) continue;
                result.Add(ReadCustomEntry(steamid, index++, entry));
            }
        }

        return result;
    }

    private static TagDef ReadCustomEntry(string steamid, int index, JObject entry)
    {
        // "updated" semantics for manual edits: a missing field OR 0 means "I just edited this,
        // apply it now" - it is treated as the current time so most-recent-wins lets the JSON edit
        // win over whatever is in the DB. After the sync writes the file back, "updated" holds the
        // real timestamp, so it is no longer forced on the next tick.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var updatedRaw = entry["updated"]?.Value<long>() ?? 0;
        var updated = updatedRaw <= 0 ? now : updatedRaw;

        return new TagDef
        {
            Id = $"{steamid}#{index}",
            Owner = steamid,
            Kind = TagKind.SteamId,
            Name = entry["name"]?.ToString() ?? "Tag",
            Prefix = entry["prefix"]?.ToString() ?? "",
            NickColor = entry["nick_color"]?.ToString() ?? "",
            MessageColor = entry["message_color"]?.ToString() ?? "",
            Scoreboard = entry["scoreboard"]?.ToString() ?? "",
            // "expires" in the file is DAYS from "updated"; 0 = permanent. Measured from the (possibly
            // forced-to-now) updated timestamp, so a freshly edited tag's expiry starts from now.
            ExpiresUnix = DaysToExpiryUnix(entry["expires"], updated),
            UpdatedUnix = updated
        };
    }

    private static long DaysToExpiryUnix(JToken? expiresDaysToken, long baseUnix)
    {
        var days = expiresDaysToken?.Value<double>() ?? 0;
        if (days <= 0) return 0; // permanent
        return baseUnix + (long)(days * 86400);
    }

    // --- MySQL sync ---------------------------------------------------------------------------

    private string ConnString()
    {
        var m = _config.Mysql;
        return new MySqlConnectionStringBuilder
        {
            Server = m.Host,
            Port = m.Port,
            UserID = m.User,
            Password = m.Password,
            Database = m.Database
        }.ConnectionString;
    }

    private string AdminTable => _config.Mysql.TableAdmin;

    private void EnsureTable()
    {
        using var conn = new MySqlConnection(ConnString());
        conn.Open();

        // Custom tags: per-SteamID lists, with expiry.
        conn.Execute($@"
            CREATE TABLE IF NOT EXISTS `{_config.Mysql.Table}` (
                `steamid`       VARCHAR(20)  NOT NULL,
                `name`          VARCHAR(64)  NOT NULL DEFAULT 'Tag',
                `prefix`        VARCHAR(128) NOT NULL DEFAULT '',
                `nick_color`    VARCHAR(32)  NOT NULL DEFAULT '',
                `message_color` VARCHAR(32)  NOT NULL DEFAULT '',
                `scoreboard`    VARCHAR(64)  NOT NULL DEFAULT '',
                `expires`       BIGINT       NOT NULL DEFAULT 0,
                `updated`       BIGINT       NOT NULL DEFAULT 0,
                PRIMARY KEY (`steamid`, `name`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

        // Admin tags: one per "#group" / "@permission" key, no expiry.
        conn.Execute($@"
            CREATE TABLE IF NOT EXISTS `{AdminTable}` (
                `tag_key`       VARCHAR(64)  NOT NULL,
                `name`          VARCHAR(64)  NOT NULL DEFAULT 'Tag',
                `prefix`        VARCHAR(128) NOT NULL DEFAULT '',
                `nick_color`    VARCHAR(32)  NOT NULL DEFAULT '',
                `message_color` VARCHAR(32)  NOT NULL DEFAULT '',
                `scoreboard`    VARCHAR(64)  NOT NULL DEFAULT '',
                `updated`       BIGINT       NOT NULL DEFAULT 0,
                PRIMARY KEY (`tag_key`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
    }

    /// <summary>
    /// Two-way sync of BOTH custom tags (DB &lt;-&gt; custom_tags.jsonc, keyed by steamid|name, with
    /// expiry) and admin tags (DB &lt;-&gt; admin_tags.jsonc, keyed by the #group/@permission key).
    /// Most-recent-wins by the per-tag "updated" timestamp. After merging, the DB, both JSON files
    /// and the in-memory set are all rewritten to the merged state.
    /// </summary>
    public void SyncOnce()
    {
        if (!_config.UseDatabase) return;

        try
        {
            SyncCustom();
            SyncAdmin();
        }
        catch (Exception ex)
        {
            _logger.LogError("[CS2-Tags] Sync failed: {Err}", ex.Message);
        }
    }

    private void SyncCustom()
    {
        // 1) Current custom tags from JSON (reload to pick up manual edits) + DB.
        var jsonTags = LoadCustomFile();
        var dbTags = ReadDbTags();

        // 2) Merge most-recent-wins, keyed by steamid|name.
        var merged = new Dictionary<string, TagDef>(StringComparer.OrdinalIgnoreCase);
        void Consider(TagDef t)
        {
            var key = $"{t.Owner}|{t.Name}".ToLowerInvariant();
            if (!merged.TryGetValue(key, out var existing) || t.UpdatedUnix >= existing.UpdatedUnix)
                merged[key] = t;
        }
        foreach (var t in jsonTags) Consider(t);
        foreach (var t in dbTags) Consider(t);

        var mergedList = merged.Values.Where(t => !t.IsExpired).ToList();

        // 3) Write back ONLY where the content actually changed, to avoid pointless DB/disk writes
        //    every tick. The DB holds no persona names, so it compares on the plain tag signature.
        //    The JSON file ALSO carries the "//name" comments, so its write decision includes the
        //    resolved persona names: when a name becomes available, the file is rewritten once to
        //    add it, then stays stable.
        var mergedSig = Signature(mergedList);
        if (Signature(dbTags) != mergedSig)
            WriteDbTags(mergedList);

        var fileSig = CustomFileSignature(mergedList);
        if (fileSig != _lastCustomFileSig)
        {
            WriteCustomFile(mergedList);
            _lastCustomFileSig = fileSig;
        }

        // 4) Refresh in-memory custom tags.
        lock (_lock)
        {
            foreach (var id in _tags.Where(kv => kv.Value.Kind == TagKind.SteamId).Select(kv => kv.Key).ToList())
                _tags.Remove(id);

            var perSteam = new Dictionary<string, int>();
            foreach (var t in mergedList)
            {
                var idx = perSteam.TryGetValue(t.Owner, out var n) ? n : 0;
                perSteam[t.Owner] = idx + 1;
                t.Id = $"{t.Owner}#{idx}";
                _tags[t.Id] = t;
            }
        }
    }

    private void SyncAdmin()
    {
        // 1) Current admin tags from JSON + DB.
        var jsonTags = LoadAdminFile();
        var dbTags = ReadDbAdminTags();

        // 2) Merge most-recent-wins, keyed by the #group/@permission key.
        var merged = new Dictionary<string, TagDef>(StringComparer.OrdinalIgnoreCase);
        void Consider(TagDef t)
        {
            if (!merged.TryGetValue(t.Owner, out var existing) || t.UpdatedUnix >= existing.UpdatedUnix)
                merged[t.Owner] = t;
        }
        foreach (var t in jsonTags) Consider(t);
        foreach (var t in dbTags) Consider(t);

        var mergedList = merged.Values.ToList();

        // 3) Write back only where the content changed (same rationale as SyncCustom).
        var mergedSig = Signature(mergedList);
        if (Signature(dbTags) != mergedSig)
            WriteDbAdminTags(mergedList);
        if (Signature(jsonTags) != mergedSig)
            WriteAdminFile(mergedList);

        // 4) Refresh in-memory admin tags.
        lock (_lock)
        {
            foreach (var id in _tags.Where(kv => kv.Value.Kind != TagKind.SteamId).Select(kv => kv.Key).ToList())
                _tags.Remove(id);

            foreach (var t in mergedList)
            {
                t.Id = t.Owner;
                _tags[t.Id] = t;
            }
        }
    }

    /// <summary>
    /// Order-independent content signature of a tag set. Two sets with the same tags (same fields)
    /// produce the same signature, so a sync tick can skip writing when nothing changed.
    /// </summary>
    private static string Signature(IEnumerable<TagDef> tags)
    {
        var lines = tags
            .Select(t => string.Join("\u0001",
                t.Owner, t.Name, t.Prefix, t.NickColor, t.MessageColor, t.Scoreboard,
                t.ExpiresUnix.ToString(), t.UpdatedUnix.ToString()))
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join("\u0002", lines);
    }

    /// <summary>
    /// Signature of what custom_tags.jsonc would contain: the tag signature PLUS the persona-name
    /// comment for each SteamID. This makes a newly-resolved Steam name trigger exactly one rewrite.
    /// </summary>
    private string CustomFileSignature(IEnumerable<TagDef> tags)
    {
        var tagsSig = Signature(tags);
        var names = tags
            .Select(t => t.Owner)
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(id => _personaNames.TryGetValue(id, out var n) ? $"{id}={n}" : id);
        return tagsSig + "\u0003" + string.Join("\u0002", names);
    }

    private List<TagDef> ReadDbTags()
    {
        using var conn = new MySqlConnection(ConnString());
        conn.Open();
        var rows = conn.Query($@"SELECT steamid, name, prefix, nick_color, message_color, scoreboard, expires, updated
                                 FROM `{_config.Mysql.Table}`");
        var list = new List<TagDef>();
        foreach (var r in rows)
        {
            list.Add(new TagDef
            {
                Owner = (string)r.steamid,
                Kind = TagKind.SteamId,
                Name = (string)r.name,
                Prefix = (string)r.prefix,
                NickColor = (string)r.nick_color,
                MessageColor = (string)r.message_color,
                Scoreboard = (string)r.scoreboard,
                ExpiresUnix = (long)r.expires,
                UpdatedUnix = (long)r.updated
            });
        }
        return list;
    }

    private void WriteDbTags(List<TagDef> tags)
    {
        using var conn = new MySqlConnection(ConnString());
        conn.Open();
        using var tx = conn.BeginTransaction();

        // Replace the whole set so deletions in JSON/DB propagate. (Dataset is small.)
        conn.Execute($"DELETE FROM `{_config.Mysql.Table}`", transaction: tx);
        foreach (var t in tags)
        {
            conn.Execute($@"INSERT INTO `{_config.Mysql.Table}`
                            (steamid, name, prefix, nick_color, message_color, scoreboard, expires, updated)
                            VALUES (@Owner, @Name, @Prefix, @NickColor, @MessageColor, @Scoreboard, @ExpiresUnix, @UpdatedUnix)",
                t, tx);
        }
        tx.Commit();
    }

    private List<TagDef> ReadDbAdminTags()
    {
        using var conn = new MySqlConnection(ConnString());
        conn.Open();
        var rows = conn.Query($@"SELECT tag_key, name, prefix, nick_color, message_color, scoreboard, updated
                                 FROM `{AdminTable}`");
        var list = new List<TagDef>();
        foreach (var r in rows)
        {
            var key = (string)r.tag_key;
            list.Add(new TagDef
            {
                Owner = key,
                Kind = key.StartsWith('@') ? TagKind.Permission
                     : key.StartsWith('#') ? TagKind.Group
                     : string.Equals(key, "everyone", StringComparison.OrdinalIgnoreCase) ? TagKind.Everyone
                     : TagKind.Group,
                Name = (string)r.name,
                Prefix = (string)r.prefix,
                NickColor = (string)r.nick_color,
                MessageColor = (string)r.message_color,
                Scoreboard = (string)r.scoreboard,
                ExpiresUnix = 0,
                UpdatedUnix = (long)r.updated
            });
        }
        return list;
    }

    private void WriteDbAdminTags(List<TagDef> tags)
    {
        using var conn = new MySqlConnection(ConnString());
        conn.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute($"DELETE FROM `{AdminTable}`", transaction: tx);
        foreach (var t in tags)
        {
            conn.Execute($@"INSERT INTO `{AdminTable}`
                            (tag_key, name, prefix, nick_color, message_color, scoreboard, updated)
                            VALUES (@Owner, @Name, @Prefix, @NickColor, @MessageColor, @Scoreboard, @UpdatedUnix)",
                t, tx);
        }
        tx.Commit();
    }

    // --- custom_tags.jsonc writing (with Steam name comments) ---------------------------------

    private void WriteCustomFile(List<TagDef> tags)
    {
        // Group by SteamID so each player becomes one "steamid": [ ... ] block.
        var bySteam = tags.GroupBy(t => t.Owner).OrderBy(g => g.Key).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        AppendColorComment(sb);
        sb.AppendLine("  // Custom tags per SteamID64. Each SteamID maps to a LIST of tags.");
        sb.AppendLine("  // \"expires\" is in DAYS from \"updated\" (0 = permanent). In DB mode this file");
        sb.AppendLine("  // is synced with MySQL every sync tick (most-recent-wins by \"updated\").");
        sb.AppendLine("  \"tags\": {");

        for (var gi = 0; gi < bySteam.Count; gi++)
        {
            var group = bySteam[gi];
            var steamid = group.Key;

            // Persona name comment above the block, if we have one.
            if (_personaNames.TryGetValue(steamid, out var persona) && !string.IsNullOrEmpty(persona))
                sb.AppendLine($"    //{persona}");

            sb.AppendLine($"    \"{steamid}\": [");
            var items = group.ToList();
            for (var i = 0; i < items.Count; i++)
            {
                var t = items[i];
                var daysLeft = ExpiryUnixToDays(t.ExpiresUnix, t.UpdatedUnix);
                sb.AppendLine("      {");
                sb.AppendLine($"        \"name\": {JsonConvert.ToString(t.Name)},");
                sb.AppendLine($"        \"prefix\": {JsonConvert.ToString(t.Prefix)},");
                sb.AppendLine($"        \"nick_color\": {JsonConvert.ToString(t.NickColor)},");
                sb.AppendLine($"        \"message_color\": {JsonConvert.ToString(t.MessageColor)},");
                sb.AppendLine($"        \"scoreboard\": {JsonConvert.ToString(t.Scoreboard)},");
                sb.AppendLine($"        \"expires\": {daysLeft},");
                sb.AppendLine($"        \"updated\": {t.UpdatedUnix}");
                sb.AppendLine(i < items.Count - 1 ? "      }," : "      }");
            }
            sb.AppendLine(gi < bySteam.Count - 1 ? "    ]," : "    ]");
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");

        File.WriteAllText(_customPath, sb.ToString());
    }

    private void WriteAdminFile(List<TagDef> tags)
    {
        // One block per "#group" / "@permission" key.
        var ordered = tags.OrderBy(t => t.Owner).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        AppendColorComment(sb);
        sb.AppendLine("  // Admin tags. Keys are \"#group\" or \"@permission\". One tag per key.");
        sb.AppendLine("  // In DB mode this file is synced with MySQL (most-recent-wins by \"updated\").");
        sb.AppendLine("  \"tags\": {");

        for (var i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            sb.AppendLine($"    {JsonConvert.ToString(t.Owner)}: {{");
            sb.AppendLine($"      \"name\": {JsonConvert.ToString(t.Name)},");
            sb.AppendLine($"      \"prefix\": {JsonConvert.ToString(t.Prefix)},");
            sb.AppendLine($"      \"nick_color\": {JsonConvert.ToString(t.NickColor)},");
            sb.AppendLine($"      \"message_color\": {JsonConvert.ToString(t.MessageColor)},");
            sb.AppendLine($"      \"scoreboard\": {JsonConvert.ToString(t.Scoreboard)},");
            sb.AppendLine($"      \"updated\": {t.UpdatedUnix}");
            sb.AppendLine(i < ordered.Count - 1 ? "    }," : "    }");
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");

        File.WriteAllText(_adminPath, sb.ToString());
    }

    private static long ExpiryUnixToDays(long expiresUnix, long updatedUnix)
    {
        if (expiresUnix == 0) return 0;
        var seconds = expiresUnix - updatedUnix;
        if (seconds <= 0) return 0;
        return (long)Math.Round(seconds / 86400.0);
    }

    // --- Steam Web API persona names ----------------------------------------------------------

    /// <summary>
    /// Resolves persona names for the given SteamIDs via the Steam Web API (needs SteamApiKey) and
    /// caches them for the comment lines. Best-effort: failures are ignored.
    /// </summary>
    public async Task ResolvePersonaNamesAsync(IEnumerable<string> steamIds)
    {
        if (string.IsNullOrWhiteSpace(_config.SteamApiKey)) return;

        var missing = steamIds.Distinct().Where(id => !_personaNames.ContainsKey(id)).ToList();
        if (missing.Count == 0) return;

        try
        {
            // Steam API accepts up to 100 ids per call.
            foreach (var batch in missing.Chunk(100))
            {
                var ids = string.Join(",", batch);
                var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={_config.SteamApiKey}&steamids={ids}";
                var resp = await _http.GetFromJsonAsync<SteamSummaryResponse>(url);
                if (resp?.response?.players == null) continue;
                foreach (var p in resp.response.players)
                    if (!string.IsNullOrEmpty(p.steamid) && !string.IsNullOrEmpty(p.personaname))
                        _personaNames[p.steamid] = p.personaname;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[CS2-Tags] Steam name lookup failed: {Err}", ex.Message);
        }
    }

    public IEnumerable<string> CustomSteamIds()
    {
        lock (_lock)
            return _tags.Values.Where(t => t.Kind == TagKind.SteamId).Select(t => t.Owner).Distinct().ToList();
    }

    private sealed class SteamSummaryResponse
    {
        public SteamResponseBody? response { get; set; }
    }
    private sealed class SteamResponseBody
    {
        public List<SteamPlayer>? players { get; set; }
    }
    private sealed class SteamPlayer
    {
        public string? steamid { get; set; }
        public string? personaname { get; set; }
    }

    // --- JSONC helpers & defaults -------------------------------------------------------------

    private JObject? ParseJsonc(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new JsonTextReader(new StreamReader(path));
            return JObject.Load(reader); // Newtonsoft ignores // and /* */ comments
        }
        catch (Exception ex)
        {
            _logger.LogError("[CS2-Tags] Failed to parse {File}: {Err}", Path.GetFileName(path), ex.Message);
            return null;
        }
    }

    private static void AppendColorComment(System.Text.StringBuilder sb)
    {
        sb.AppendLine("  // Available colors:");
        sb.AppendLine("  // {default} {white} {darkred} {green} {lightyellow} {lightblue} {olive}");
        sb.AppendLine("  // {lime} {red} {lightpurple} {purple} {grey} {yellow} {gold} {silver}");
        sb.AppendLine("  // {blue} {darkblue} {bluegrey} {magenta} {lightred} {orange} {teamcolor}");
    }

    private void EnsureDefaultFiles()
    {
        if (!File.Exists(_adminPath))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            AppendColorComment(sb);
            sb.AppendLine("  // Admin tags. Keys are \"#group\" or \"@permission\". One tag per key.");
            sb.AppendLine("  // Fields: name, prefix, nick_color, message_color, scoreboard.");
            sb.AppendLine("  \"tags\": {");
            sb.AppendLine("    \"#css/admin\": {");
            sb.AppendLine("      \"name\": \"Admin\",");
            sb.AppendLine("      \"prefix\": \"{red}[ADMIN]\",");
            sb.AppendLine("      \"nick_color\": \"{red}\",");
            sb.AppendLine("      \"message_color\": \"{gold}\",");
            sb.AppendLine("      \"scoreboard\": \"[ADMIN]\"");
            sb.AppendLine("    },");
            sb.AppendLine("    \"@css/chat\": {");
            sb.AppendLine("      \"name\": \"Chat Moderator\",");
            sb.AppendLine("      \"prefix\": \"{green}[CHAT]\",");
            sb.AppendLine("      \"nick_color\": \"{red}\",");
            sb.AppendLine("      \"message_color\": \"{gold}\",");
            sb.AppendLine("      \"scoreboard\": \"[CHAT]\"");
            sb.AppendLine("    },");
            sb.AppendLine("    // \"everyone\" applies to all players (bots excluded).");
            sb.AppendLine("    // {userid} is replaced with the player's in-game user id.");
            sb.AppendLine("    \"everyone\": {");
            sb.AppendLine("      \"name\": \"Player\",");
            sb.AppendLine("      \"prefix\": \"\",");
            sb.AppendLine("      \"nick_color\": \"\",");
            sb.AppendLine("      \"message_color\": \"\",");
            sb.AppendLine("      \"scoreboard\": \"#{userid}\"");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            File.WriteAllText(_adminPath, sb.ToString());
        }

        if (!File.Exists(_customPath))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            AppendColorComment(sb);
            sb.AppendLine("  // Custom tags per SteamID64. Each SteamID maps to a LIST of tags.");
            sb.AppendLine("  // \"expires\" is in DAYS from \"updated\" (0 = permanent).");
            sb.AppendLine("  \"tags\": {");
            sb.AppendLine("    //ExamplePlayer");
            sb.AppendLine("    \"76561197961430531\": [");
            sb.AppendLine("      {");
            sb.AppendLine("        \"name\": \"Owner\",");
            sb.AppendLine("        \"prefix\": \"{gold}[OWNER]\",");
            sb.AppendLine("        \"nick_color\": \"{gold}\",");
            sb.AppendLine("        \"message_color\": \"{yellow}\",");
            sb.AppendLine("        \"scoreboard\": \"[OWNER]\",");
            sb.AppendLine("        \"expires\": 0,");
            sb.AppendLine("        \"updated\": 0");
            sb.AppendLine("      }");
            sb.AppendLine("    ]");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            File.WriteAllText(_customPath, sb.ToString());
        }
    }
}
