// Generated from the bundled engines' LOOKS and swatchOf (Drawer/Characters); LookCatalogTests
// checks every row against the engines the app ships. Each kind's default look comes first.
namespace AgentIsland.Model;

public static partial class LookCatalog
{
    private static readonly Dictionary<(CharacterTheme, string), Look[]> Data = new()
    {
        [(CharacterTheme.Pixel, "claude")] = new Look[] { new("clawd", "Clawd", 0xD77757) },
        [(CharacterTheme.Pixel, "codex")] = new Look[] { new("notebook", "Notebook", 0x9EA1AB), new("puff", "Puff cloud", 0xA2A5AF), new("cloud", "Cloud", 0x8E919C), new("folio", "Folio", 0x8D94A6), new("scroll", "Scroll", 0xA0998A), new("owl", "Owl", 0x4D96EB) },
        [(CharacterTheme.Pixel, "antigravity")] = new Look[] { new("balloon", "Balloon", 0x3D7BF5), new("rainbow", "Rainbow A", 0x2FA3D6), new("aeri", "Aeri", 0x4D96EB), new("blocks", "Blocks", 0x3166A4), new("kite", "Kite", 0x5AA0F0), new("otter", "Sea otter", 0xA8742E) },
        [(CharacterTheme.Pixel, "grok")] = new Look[] { new("ring", "Slashed ring", 0x0B0B0C), new("ringbot", "Ring bot", 0x2A2A30), new("horizon", "Event horizon", 0x3B3227), new("cat", "Black cat", 0x1A1A1F), new("astronaut", "Astronaut", 0x5E616C), new("martian", "Martian", 0x3A3A42) },
        [(CharacterTheme.Pixel, "cursor")] = new Look[] { new("ring", "Slashed ring", 0x0B0B0C), new("ringbot", "Ring bot", 0x2A2A30), new("horizon", "Event horizon", 0x3B3227), new("cat", "Black cat", 0x1A1A1F), new("astronaut", "Astronaut", 0x5E616C), new("martian", "Martian", 0x3A3A42) },
        [(CharacterTheme.Pixel, "kimi")] = new Look[] { new("blob", "Blob and terminal", 0x2B8BFF), new("rabbit", "Moon rabbit", 0x5AA6FF), new("terminal", "Terminal pal", 0x1F6FD6), new("moonlet", "Moonlet", 0x4A9BFF), new("rocket", "Rocket", 0x2B7BEF) },
        [(CharacterTheme.Pixel, "deepseek")] = new Look[] { new("whale", "Whale", 0x4D6BFE) },
        [(CharacterTheme.Classic, "claude")] = new Look[] { new("succulent", "Succulent", 0xD97757), new("clay-timer", "Clay timer", 0xD97757) },
        [(CharacterTheme.Classic, "codex")] = new Look[] { new("desk-computer", "Desk computer", 0x2D8CFF), new("coffee-mug", "Coffee mug", 0x2D8CFF), new("keycap", "Keycap", 0x2D8CFF), new("cloud-cushion", "Cloud cushion", 0x2D8CFF), new("cloud-terminal", "Cloud terminal", 0x2D8CFF) },
        [(CharacterTheme.Classic, "antigravity")] = new Look[] { new("flying-saucer", "Flying saucer", 0x8E63F5), new("paper-plane", "Paper plane", 0x8E63F5), new("crowned-arch", "Crowned arch", 0x8E63F5) },
        [(CharacterTheme.Classic, "grok")] = new Look[] { new("ringed-planet", "Ringed planet", 0x6E7480), new("orbit-ring", "Orbit ring", 0x6E7480) },
        [(CharacterTheme.Classic, "cursor")] = new Look[] { new("ringed-planet", "Ringed planet", 0x24252C), new("orbit-ring", "Orbit ring", 0x24252C) },
        [(CharacterTheme.Classic, "kimi")] = new Look[] { new("astronaut-helmet", "Astronaut helmet", 0x1D4E9E), new("moon-lamp", "Moon lamp", 0x1D4E9E), new("moon-rabbit", "Moon rabbit", 0x1D4E9E), new("moonshot-rocket", "Moonshot rocket", 0x1D4E9E), new("note-clip", "Note clip", 0x1D4E9E) },
        [(CharacterTheme.Classic, "deepseek")] = new Look[] { new("submarine", "Submarine", 0x4D6BFE), new("whale-stapler", "Whale stapler", 0x4D6BFE), new("tape-whale", "Tape whale", 0x4D6BFE) },
    };
}
