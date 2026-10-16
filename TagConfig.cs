using Newtonsoft.Json;

namespace CS2_Tags;

/// <summary>
/// General CS2-Tags config, stored as configs/cs2-tags.jsonc inside the plugin folder.
/// </summary>
public sealed class TagConfig
{
    /// <summary>
    /// "local" = tags come only from the .jsonc files.
    /// "db"    = MySQL is the shared store; custom tags are synced both ways (DB &lt;-&gt; custom_tags.jsonc)
    ///            every SyncIntervalSeconds, most-recent-wins by the per-tag "updated" timestamp.
    /// Admin tags always come from admin_tags.jsonc regardless of this setting.
    /// </summary>
    [JsonProperty("Storage")]
    public string Storage { get; set; } = "local";

    [JsonProperty("Mysql")]
    public MysqlConfig Mysql { get; set; } = new();

    /// <summary>How often (seconds) to sync custom tags with the DB in "db" mode. Minimum 10.</summary>
    [JsonProperty("SyncIntervalSeconds")]
    public int SyncIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Steam Web API key. When set, CS2-Tags resolves the persona name for each custom SteamID so
    /// it can write a "// name" comment above the block in custom_tags.jsonc (works for offline
    /// players too). Leave empty to skip name comments.
    /// </summary>
    [JsonProperty("SteamApiKey")]
    public string SteamApiKey { get; set; } = "";

    public bool UseDatabase => string.Equals(Storage, "db", StringComparison.OrdinalIgnoreCase);
}

public sealed class MysqlConfig
{
    [JsonProperty("Host")] public string Host { get; set; } = "127.0.0.1";
    [JsonProperty("Port")] public uint Port { get; set; } = 3306;
    [JsonProperty("User")] public string User { get; set; } = "root";
    [JsonProperty("Password")] public string Password { get; set; } = "";
    [JsonProperty("Database")] public string Database { get; set; } = "cs2";

    /// <summary>Table holding custom (per-SteamID) tags.</summary>
    [JsonProperty("Table")] public string Table { get; set; } = "cs2tags_custom";

    /// <summary>Table holding admin (#group / @permission) tags.</summary>
    [JsonProperty("TableAdmin")] public string TableAdmin { get; set; } = "cs2tags_admin";
}
