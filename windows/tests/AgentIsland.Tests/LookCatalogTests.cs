using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentIsland.Core;
using AgentIsland.Model;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace AgentIsland.Tests;

/// LookCatalog (the looks each agent can wear, and their colours) against the
/// two engines the app ships, run in V8 as the drawer runs them: every look
/// the engine lists is in the catalog with its name and colour, default first,
/// and every look draws. Also the look keys in the settings file, the
/// fallback for an id no engine knows, and the pixel faces.
public static class LookCatalogTests
{
    private sealed record EngineLook(string Id, string Name, uint Swatch, bool Draws);

    public static void RunAll()
    {
        foreach (var (theme, file) in new[] { (CharacterTheme.Pixel, "characters.mjs"), (CharacterTheme.Classic, "classic.mjs") })
        {
            var engine = EngineLooks(file);
            Expect(engine.Count == 7, $"{theme}: the engine lists looks for all seven kinds");
            foreach (var provider in DisplayProviders.All)
            {
                var kind = DrawerLooks.Kind(provider);
                var mirror = LookCatalog.For(theme, provider);
                var listed = engine[kind];
                Expect(listed.Count > 0 && mirror.Count == listed.Count,
                    $"{theme} {kind}: the catalog has the engine's {listed.Count} looks, got {mirror.Count}");
                for (var i = 0; i < listed.Count; i++)
                {
                    Expect(mirror[i].Id == listed[i].Id && mirror[i].Name == listed[i].Name,
                        $"{theme} {kind} look {i}: the engine's '{listed[i].Id}' ({listed[i].Name}), got '{mirror[i].Id}' ({mirror[i].Name})");
                    Expect(mirror[i].Swatch == listed[i].Swatch,
                        $"{theme} {kind} '{listed[i].Id}': the engine's #{listed[i].Swatch:X6}, got #{mirror[i].Swatch:X6}");
                    Expect(listed[i].Draws, $"{theme} {kind} '{listed[i].Id}' draws a frame");
                    Expect(Regex.IsMatch(listed[i].Id, "^[a-z0-9-]+$"), $"{theme} {kind} '{listed[i].Id}' is a saveable id");
                }
                Expect(listed.Select(l => l.Id).Distinct().Count() == listed.Count, $"{theme} {kind}: ids are unique");
                Expect(LookCatalog.Resolve(theme, provider, null) == mirror[0]
                    && LookCatalog.Resolve(theme, provider, "withdrawn-look") == mirror[0]
                    && LookCatalog.Resolve(theme, provider, mirror[^1].Id) == mirror[^1],
                    $"{theme} {kind}: no id or an unknown one reads as the default; a known one as itself");
            }
            Expect(LookCatalog.For(theme, DisplayProvider.Cursor).Select(l => l.Id).SequenceEqual(LookCatalog.For(theme, DisplayProvider.Grok).Select(l => l.Id)),
                $"{theme}: Cursor's looks carry Grok's ids");
            Console.WriteLine($"PASS {theme}: every look, name and colour matches the engine, and each draws");
        }

        // The defaults are today's colours; Cursor wears Grok's in Pixel, its own black in Classic.
        Expect(LookCatalog.For(CharacterTheme.Pixel, DisplayProvider.Claude)[0].Swatch == 0xD77757
            && LookCatalog.For(CharacterTheme.Classic, DisplayProvider.Claude)[0].Swatch == 0xD97757
            && LookCatalog.For(CharacterTheme.Pixel, DisplayProvider.Cursor)[0].Swatch == LookCatalog.For(CharacterTheme.Pixel, DisplayProvider.Grok)[0].Swatch
            && LookCatalog.For(CharacterTheme.Classic, DisplayProvider.Cursor)[0].Swatch == 0x24252C,
            "the default looks keep today's colours");
        Console.WriteLine("PASS the default looks keep today's colours");

        var pixelKey = DrawerStore.LookKey(CharacterTheme.Pixel, DisplayProvider.Kimi);
        var classicKey = DrawerStore.LookKey(CharacterTheme.Classic, DisplayProvider.Kimi);
        Expect(pixelKey == "AgentIsland.drawer.look.pixel.kimi" && classicKey == "AgentIsland.drawer.look.classic.kimi",
            $"a pick is saved per set and agent, got {pixelKey} and {classicKey}");
        Expect(DrawerStore.LookKey(CharacterTheme.Pixel, DisplayProvider.Cursor) == DrawerStore.LookKey(CharacterTheme.Pixel, DisplayProvider.Grok),
            "Cursor shares Grok's pick");
        Expect(pixelKey != "AgentIsland.drawer.look.kimi", "the WPF app's shape/colour key stays its own");
        Console.WriteLine("PASS look keys are per set and agent, and apart from the WPF drawer's");

        foreach (var swatch in LookCatalog.AllSwatches())
        {
            foreach (var dark in new[] { false, true })
            {
                var color = LookCatalog.OnPaper(swatch, dark);
                var luma = Luma(color);
                Expect(dark ? luma >= 164.5 : luma <= 118.5, $"#{swatch:X6} on {(dark ? "dark" : "light")} paper reads as #{color:X6} (luma {luma:0})");
                var inBand = dark ? Luma(swatch) >= 165 : Luma(swatch) <= 118;
                Expect((swatch == color) == inBand, $"#{swatch:X6} already in band stays as is");
            }
        }
        Console.WriteLine("PASS every look's colour is pulled into the paper's lightness band");

        // Each pixel look but the default has a face (Cursor wears Grok's).
        var faces = Regex.Matches(File.ReadAllText(Source("Shell", "PixelIconData.Looks.cs")), @"\[\(DisplayProvider\.(\w+), ""([a-z0-9-]+)""\)\]")
            .Select(m => (Provider: m.Groups[1].Value, Id: m.Groups[2].Value))
            .ToHashSet();
        var wanted = DisplayProviders.All.Where(p => p.HasCharacter())
            .SelectMany(p => LookCatalog.For(CharacterTheme.Pixel, p).Skip(1).Select(l => (Provider: p.ToString(), l.Id)))
            .ToHashSet();
        Expect(faces.SetEquals(wanted),
            $"each pixel look but the default has one face: missing {string.Join(",", wanted.Except(faces))}, extra {string.Join(",", faces.Except(wanted))}");
        Console.WriteLine("PASS every non-default pixel look has a face");
        // Reduced motion holds the flickering offline of three Pixel looks still, besides
        // resting and Pixel's sleeping; nothing moves for others, or without reduced motion.
        Expect(DrawerLooks.HoldsStill("offline", CharacterTheme.Pixel, true, "terminal")
            && DrawerLooks.HoldsStill("offline", CharacterTheme.Pixel, true, "astronaut")
            && DrawerLooks.HoldsStill("offline", CharacterTheme.Pixel, true, "puff")
            && !DrawerLooks.HoldsStill("offline", CharacterTheme.Pixel, true, "notebook")
            && !DrawerLooks.HoldsStill("offline", CharacterTheme.Pixel, true, null)
            && !DrawerLooks.HoldsStill("offline", CharacterTheme.Classic, true, "terminal")
            && !DrawerLooks.HoldsStill("offline", CharacterTheme.Pixel, false, "terminal")
            && DrawerLooks.HoldsStill("resting", CharacterTheme.Classic, true, null)
            && DrawerLooks.HoldsStill("sleeping", CharacterTheme.Pixel, true, "ring")
            && !DrawerLooks.HoldsStill("sleeping", CharacterTheme.Classic, true, null),
            "reduced motion holds the flickering offline states, resting and Pixel's sleeping still");
        Console.WriteLine("PASS reduced motion holds the flickering offline states still");
        Console.WriteLine("LookCatalogTests GREEN");
    }

    private static double Luma(uint rgb) => 0.2126 * (rgb >> 16 & 0xFF) + 0.7152 * (rgb >> 8 & 0xFF) + 0.0722 * (rgb & 0xFF);

    /// The engine's own list, as the drawer's host module reads it: per kind,
    /// each look's id, name and swatchOf, and whether a bot in it draws.
    private static Dictionary<string, List<EngineLook>> EngineLooks(string file)
    {
        var directory = Path.GetDirectoryName(Source("Drawer", "Characters", file))!;
        using var v8 = new V8ScriptEngine();
        v8.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
        v8.Execute(new DocumentInfo(new Uri(Path.Combine(directory, "probe.mjs"))) { Category = ModuleCategory.Standard }, $$"""
            import * as lib from './{{file}}';
            globalThis.probe = JSON.stringify(Object.fromEntries(lib.KINDS.map(kind => [kind, lib.LOOKS[kind].map(({ id, name }) => {
                const bot = lib.createBot(kind, { seed: 7, tempo: 1, rim: true, look: id });
                bot.advance(0.1);
                return { id, name, swatch: lib.swatchOf(kind, id), draws: bot.svg(44, 'p').startsWith('<svg') };
            })])));
            """);
        using var json = JsonDocument.Parse((string)v8.Script.probe);
        return json.RootElement.EnumerateObject().ToDictionary(kind => kind.Name,
            kind => kind.Value.EnumerateArray()
                .Select(look => new EngineLook(look.GetProperty("id").GetString()!, look.GetProperty("name").GetString()!,
                    look.GetProperty("swatch").GetUInt32(), look.GetProperty("draws").GetBoolean()))
                .ToList());
    }

    /// A source file of the WinUI app, found from the test binaries up to the repo.
    private static string Source(params string[] path)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(new[] { dir.FullName, "src", "AgentIsland.WinUI" }.Concat(path).ToArray());
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException(string.Join("/", path));
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
