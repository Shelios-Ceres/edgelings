using System.IO;
using System.Text.RegularExpressions;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;

namespace AgentIsland.Tests;

/// The Classic theme's rules in DrawerLooks against the engine the app ships
/// (AgentIsland.WinUI/Drawer/Characters/classic.mjs): every phase plays a
/// state the engine has, the calm and one-shot sets, the default looks, the
/// fidgets and the palette match, and every shape has a name in the menus.
public static class ClassicThemeTests
{
    public static void RunAll()
    {
        var engine = File.ReadAllText(EngineFile());
        var phases = List(engine, "PHASES");
        var calm = List(engine, "CALM");
        var settle = Regex.Matches(Block(engine, "SETTLE"), @"(\w+): \['(\w+)'")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var kinds = Regex.Matches(Block(engine, "KIND"), @"(\w+): \{ shape: '(\w+)', color: '(\w+)', fidgets: \[([^\]]*)\] \}")
            .ToDictionary(m => m.Groups[1].Value, m => (Shape: m.Groups[2].Value, Color: m.Groups[3].Value,
                Fidgets: Regex.Matches(m.Groups[4].Value, @"'(\w+)'").Select(f => f.Groups[1].Value).ToList()));
        var palette = Regex.Matches(Block(engine, "COLOR_HEX"), @"(\w+): '#([0-9A-F]{6})'")
            .ToDictionary(m => m.Groups[1].Value, m => Convert.ToUInt32(m.Groups[2].Value, 16));
        var shapes = Regex.Matches(Block(engine, "SHAPES"), @"\n  (\w+): \{\n    d: ")
            .Select(m => m.Groups[1].Value)
            .ToList();
        var loops = Regex.Matches(Block(engine, "CYCLE"), @"(\w+): [\d.]+")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        Expect(phases.Count > 20 && calm.Count > 0 && settle.Count > 0 && kinds.Count == 7 && palette.Count == 11
            && shapes.Count == 12 && loops.Count > 20, "the engine file parses");

        foreach (var phase in Enum.GetValues<AgentPhase>())
        {
            var entry = DrawerLooks.Entry(phase, CharacterTheme.Classic);
            var hold = DrawerLooks.Hold(phase, CharacterTheme.Classic);
            Expect(phases.Contains(entry), $"{phase} enters '{entry}', which the engine has");
            Expect(phases.Contains(hold), $"{phase} rests in '{hold}', which the engine has");
            if (settle.TryGetValue(entry, out var settled))
                Expect(settled == hold, $"{phase} rests where the engine settles '{entry}' ({settled}), got {hold}");
        }
        Console.WriteLine("PASS every phase plays a Classic state the engine has");

        foreach (var state in phases)
        {
            Expect(DrawerLooks.IsOneShot(state, CharacterTheme.Classic) == settle.ContainsKey(state), $"'{state}' one-shot matches the engine");
            Expect(DrawerLooks.IsCalm(state, CharacterTheme.Classic) == calm.Contains(state), $"'{state}' calm matches the engine");
        }
        Console.WriteLine("PASS calm and one-shot states match the engine");

        foreach (var provider in DisplayProviders.All)
        {
            var kind = DrawerLooks.Kind(provider);
            Expect(kinds.TryGetValue(kind, out var look), $"the engine has a {kind} bot");
            Expect(DrawerLooks.Default(provider) == (look.Shape, look.Color), $"{provider}'s default look is the engine's {kind}");
            Expect(look.Fidgets.Count > 0, $"{provider} has something to do when idle");
            foreach (var fidget in look.Fidgets)
            {
                Expect(loops.Contains(fidget) && !DrawerLooks.IsOneShot(fidget, CharacterTheme.Classic),
                    $"{provider}'s fidget '{fidget}' is a looping state the engine draws");
            }
        }
        Console.WriteLine("PASS each provider's default look and fidgets match the engine");

        foreach (var (color, hex) in palette)
        {
            Expect(DrawerLooks.Swatch(color) == hex, $"'{color}' is the engine's #{hex:X6}");
            Expect(DrawerLooks.ColorLabel(color) != "", $"'{color}' has a label");
        }
        Expect(DrawerLooks.Swatch(DisplayProvider.Claude, "green", CharacterTheme.Classic) == palette["green"],
            "the Classic theme uses the picked colour");
        Console.WriteLine("PASS the palette matches the engine");

        var language = L10n.Current;
        L10n.Current = L10n.Language.SimplifiedChinese;
        try
        {
            foreach (var shape in shapes)
            {
                var label = DrawerLooks.ShapeLabel(shape);
                Expect(label != char.ToUpperInvariant(shape[0]) + shape[1..], $"shape '{shape}' has a Chinese name");
            }
            foreach (var color in palette.Keys)
            {
                var label = DrawerLooks.ColorLabel(color);
                Expect(label != char.ToUpperInvariant(color[0]) + color[1..], $"colour '{color}' has a Chinese name");
            }
        }
        finally
        {
            L10n.Current = language;
        }
        Console.WriteLine("PASS every shape and colour has a name in the menus");
        Console.WriteLine("ClassicThemeTests GREEN");
    }

    /// The engine as shipped, found from the test binaries up to the repo.
    private static string EngineFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "src", "AgentIsland.WinUI", "Drawer", "Characters", "classic.mjs");
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException("the shipped Classic engine (Drawer/Characters/classic.mjs)");
    }

    private static List<string> List(string engine, string name) =>
        Regex.Matches(Regex.Match(engine, $@"export const {name} = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value, @"'(\w+)'")
            .Select(m => m.Groups[1].Value)
            .ToList();

    /// The body of a top-level `const NAME = { ... };`, up to its closing line.
    private static string Block(string engine, string name) =>
        Regex.Match(engine, $@"\nconst {name} = \{{(.*?)\n?\}};", RegexOptions.Singleline).Groups[1].Value;

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
