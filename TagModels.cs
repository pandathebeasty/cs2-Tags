namespace CS2_Tags;

/// <summary>Where a tag comes from, which decides how a player qualifies for it.</summary>
public enum TagKind
{
    SteamId,
    Group,
    Permission,
    Everyone
}

/// <summary>
/// One tag definition. For admin tags the owner is the "#group" / "@permission" key; for custom
/// tags the owner is a SteamID64 and several tags may share the same SteamID (a list).
/// </summary>
public sealed class TagDef
{
    /// <summary>Stable id used by the menu / PlayerSettings choice. For admin tags = the key
    /// ("#group" or "@permission"); for custom tags = "steamid#index".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The qualifier key: SteamID64, "#group" or "@permission".</summary>
    public string Owner { get; set; } = string.Empty;

    public TagKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string NickColor { get; set; } = string.Empty;
    public string MessageColor { get; set; } = string.Empty;
    public string Scoreboard { get; set; } = string.Empty;

    /// <summary>Expiry for custom tags, as a unix timestamp (seconds). 0 = permanent. Admin tags
    /// are always 0.</summary>
    public long ExpiresUnix { get; set; }

    /// <summary>Last-modified unix timestamp (seconds), used for most-recent-wins sync.</summary>
    public long UpdatedUnix { get; set; }

    public bool IsExpired => ExpiresUnix != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresUnix;
}
