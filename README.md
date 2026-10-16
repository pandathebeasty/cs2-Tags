# CS2-Tags

> Based on [daffyyyy/CS2-Tags](https://github.com/daffyyyy/CS2-Tags) — this is a fork that reworks it
> into a ChatManager provider with local-file / MySQL storage.

Adds selectable chat/scoreboard tags to the server, assigned by **group** (`#css/...`), **permission**
(`@css/...`) or **SteamID64**. Players pick their tag with `!tags`.

This build is a **tag provider for ChatManager** — it does not hook chat or write the scoreboard
itself. All rendering, tag merging, colors and gag/silence handling are done by ChatManager, so
CS2-Tags coexists cleanly with other tag/VIP/admin plugins.

### Requirements
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/) (.NET 10)
- **[ChatManager](https://github.com/pandathebeasty/cs2-ChatManager)** — required. CS2-Tags
  registers through ChatManager's shared API (`chatmanager:api`); without it the plugin does nothing.
- **[PlayerSettings](https://github.com/NickFox007/PlayerSettingsCS2)** — recommended. Persists each
  player's selected tag between sessions. Without it, tag choices are kept for the current session only.
- **[AnyBaseLib](https://github.com/NickFox007/AnyBaseLibCS2)** — required by PlayerSettings.

### Commands
- `css_tags` — open the tag selection menu (`!tags`)
- `css_tags_reload` — reload the tag config files (and sync in DB mode)

### Storage modes
CS2-Tags supports two storage modes, set by `"Storage"` in `configs/cs2-tags.jsonc`:

**1. Local files (`"Storage": "local"`)** — the default.
Tags live only in the commented `.jsonc` files in the plugin's `configs/` folder:
- `admin_tags.jsonc` — tags by `#group` / `@permission`
- `custom_tags.jsonc` — per-SteamID64 tags

**2. MySQL database (`"Storage": "db"`)**
MySQL is the shared store. Custom tags are synced **both ways** between the database and
`custom_tags.jsonc` every `SyncIntervalSeconds` (most-recent-wins, by each tag's `updated`
timestamp), so changes made in-game, in the file, or on another server all converge. Admin tags
always come from `admin_tags.jsonc`.

### Configuration — `configs/cs2-tags.jsonc`
```jsonc
{
  // "local" = only the .jsonc files | "db" = MySQL shared store (two-way sync)
  "Storage": "local",
  "SyncIntervalSeconds": 60,
  // Optional Steam Web API key: writes a "// name" comment above each SteamID block.
  "SteamApiKey": "",
  "Mysql": {
    "Host": "127.0.0.1",
    "Port": 3306,
    "User": "root",
    "Password": "",
    "Database": "cs2",
    "Table": "cs2tags_custom",
    "TableAdmin": "cs2tags_admin"
  }
}
```

### Tag definition
Each tag has a chat prefix, a name color, a message color and a scoreboard tag. Colors use tokens
like `{red}`, `{gold}`, `{teamcolor}` and are resolved by ChatManager.
```jsonc
{
  "#css/admin":        { "prefix": "{green}[ADMIN]", "nick_color": "{red}",  "message_color": "{gold}", "scoreboard": "[ADMIN]" },
  "@css/chat":         { "prefix": "{green}[VIP]",   "nick_color": "{lime}", "message_color": "{white}","scoreboard": "[VIP]" },
  "76561198202892670": { "prefix": "{gold}[OWNER]",  "nick_color": "{gold}", "message_color": "{white}","scoreboard": "[OWNER]" },
  "everyone":          { "prefix": "",               "nick_color": "",       "message_color": "",       "scoreboard": "#{userid}" }
}
```

### Special keys & variables
- **`everyone`** — a tag applied to all players (bots excluded). Define it in `admin_tags.jsonc`.
- **`{userid}`** — a per-player variable, replaced in the prefix and scoreboard with the player's
  in-game user id (inspired by [CS2UserIDTags](https://github.com/criskkky/CS2UserIDTags)). Useful
  for showing an id next to the name, e.g. `"scoreboard": "#{userid}"`.

### Colors
`default, white, darkred, green, lightyellow, lightblue, olive, lime, red, lightpurple, purple,
grey, yellow, gold, silver, blue, darkblue, bluegrey, magenta, lightred, orange` and `teamcolor`.
Use the color name inside braces, e.g. `{lightred}`.
