using System.IO;
using System.Text.RegularExpressions;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Tests;

/// The pixel theme's rules in DrawerLooks against the engine the app ships
/// (AgentIsland.WinUI/Drawer/Characters/characters.mjs): every phase plays a
/// state the engine has, and the calm and one-shot sets and the colours
/// match.
public static class PixelThemeTests
{
    public static void RunAll()
    {
        var engine = File.ReadAllText(EngineFile());
        var phases = List(engine, "PHASES");
        var calm = List(engine, "CALM");
        var kinds = List(engine, "KINDS");
        var settle = Regex.Matches(Regex.Match(engine, @"const SETTLE = \{(.*?)\};").Groups[1].Value, @"(\w+): \['(\w+)'")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var swatches = Regex.Matches(engine, @"CHARACTERS\.(\w+) = \{[^}]*?swatch: 0x([0-9A-Fa-f]{6})", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => Convert.ToUInt32(m.Groups[2].Value, 16));
        Expect(phases.Count > 20 && calm.Count > 0 && kinds.Count == 7 && settle.Count > 0 && swatches.Count == 7,
            "the engine file parses");

        foreach (var phase in Enum.GetValues<AgentPhase>())
        {
            var entry = DrawerLooks.Entry(phase, CharacterTheme.Pixel);
            var hold = DrawerLooks.Hold(phase, CharacterTheme.Pixel);
            Expect(phases.Contains(entry), $"{phase} enters '{entry}', which the engine has");
            Expect(phases.Contains(hold), $"{phase} rests in '{hold}', which the engine has");
            if (settle.TryGetValue(entry, out var settled))
                Expect(settled == hold, $"{phase} rests where the engine settles '{entry}' ({settled}), got {hold}");
        }
        Console.WriteLine("PASS every phase plays a pixel state the engine has");

        foreach (var state in phases)
        {
            Expect(DrawerLooks.IsOneShot(state, CharacterTheme.Pixel) == settle.ContainsKey(state), $"'{state}' one-shot matches the engine");
            Expect(DrawerLooks.IsCalm(state, CharacterTheme.Pixel) == calm.Contains(state), $"'{state}' calm matches the engine");
        }
        Console.WriteLine("PASS calm and one-shot states match the engine");

        var bots = DisplayProviders.All.Select(DrawerLooks.Kind).ToList();
        Expect(bots.Distinct().Count() == bots.Count && bots.All(kinds.Contains) && bots.Count == kinds.Count,
            "each provider has its own pixel bot");
        foreach (var provider in DisplayProviders.All)
        {
            var kind = DrawerLooks.Kind(provider);
            Expect(DrawerLooks.PixelSwatch(provider) == swatches[kind], $"{provider}'s colour matches the engine's {kind}");
        }
        Expect(DrawerLooks.Swatch(DisplayProvider.Claude, CharacterTheme.Pixel) == 0xD77757, "the pixel theme uses the bot's own colour");
        Console.WriteLine("PASS each provider's pixel bot and colour match the engine");

        // The pixel panels read in English inside L10n.English(), and only there.
        var language = L10n.Current;
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            Expect(L10n.Tr("Not scanned yet") == "还没扫描", "the app language holds outside the scope");
            using (L10n.English())
            {
                Expect(!L10n.IsChinese && L10n.Tr("Not scanned yet") == "Not scanned yet", "English inside the scope");
                using (L10n.English()) { }
                Expect(!L10n.IsChinese, "a nested scope closing keeps English");
                Expect(Task.Run(() => L10n.IsChinese).Result, "other threads keep the app language");
            }
            Expect(L10n.IsChinese, "the app language returns after the scope");
            var stored = L10n.Tr("open Kimi Code to sync its plan usage");
            Expect(L10n.Relocalize(stored) == stored, "a caption stored in Chinese reads as is in Chinese");
            using (L10n.English())
            {
                Expect(L10n.Relocalize(stored) == "open Kimi Code to sync its plan usage", "and in English inside the scope");
                Expect(L10n.Relocalize("Bash") == "Bash", "text that is no table entry is left alone");
            }
        }
        finally
        {
            L10n.Current = language;
        }
        Console.WriteLine("PASS the pixel panels' English stays inside its scope");
        Console.WriteLine("PixelThemeTests GREEN");
    }

    /// The engine as shipped, found from the test binaries up to the repo.
    private static string EngineFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "src", "AgentIsland.WinUI", "Drawer", "Characters", "characters.mjs");
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException("the shipped pixel engine (Drawer/Characters/characters.mjs)");
    }

    private static List<string> List(string engine, string name) =>
        Regex.Matches(Regex.Match(engine, $@"export const {name} = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value, @"'(\w+)'")
            .Select(m => m.Groups[1].Value)
            .ToList();

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
