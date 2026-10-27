using AgentIsland.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AgentIsland.WinUI.Drawer;

/// Looks drawn outside the drawer, for the pickers: a tile's character and a
/// menu item's icon. A bot per look sits in an engine of its own, one per
/// character set, made when a picker first asks and kept until Settings
/// closes (Release). Everything here runs on the UI thread.
internal static class LookPreviews
{
    // The same seed for every preview, so a look always starts alike.
    private const int Seed = 7;
    private const double Settled = 0.5;
    private const double Box = 44;

    private static readonly Dictionary<CharacterTheme, ICharacterEngine?> Engines = new();
    // Two bots per look: one that plays, and one that stays at its still.
    private static readonly Dictionary<(CharacterTheme, DisplayProvider, string, bool Still), int> Bots = new();
    private static readonly Dictionary<(CharacterTheme, DisplayProvider, string, double, double), ImageSource> Stills = new();
    private static readonly SvgCanvas Canvas = new();

    private static ICharacterEngine? Engine(CharacterTheme theme)
    {
        if (Engines.TryGetValue(theme, out var made)) return made;
        ICharacterEngine? engine = null;
        try
        {
            engine = V8CharacterEngine.Create(theme, V8CharacterEngine.Directory);
        }
        catch (Exception error)
        {
            CrashLog.Write(error, "Look previews");
        }
        return Engines[theme] = engine;
    }

    private static int Bot(CharacterTheme theme, DisplayProvider provider, string look, bool still, ICharacterEngine engine)
    {
        if (Bots.TryGetValue((theme, provider, look, still), out var index)) return index;
        index = engine.AddBot(provider, DrawerLooks.TempoOf(provider), Seed, look);
        engine.SetState(index, "idle", 0);
        engine.Tick(Advance(index, Settled), 44);
        return Bots[(theme, provider, look, still)] = index;
    }

    private static double[] Advance(int index, double dt)
    {
        var dts = new double[index + 1];
        Array.Fill(dts, double.NaN);
        dts[index] = dt;
        return dts;
    }

    /// The look's idle frame, taken Settled seconds in: the same every time,
    /// however far a tile has played the look.
    public static string? Still(CharacterTheme theme, DisplayProvider provider, string look, double size) =>
        Frame(theme, provider, look, still: true, 0, size);

    /// The look's idle animation moved on by dt seconds (0 repeats the frame
    /// it is at); null without an engine.
    public static string? Step(CharacterTheme theme, DisplayProvider provider, string look, double dt, double size) =>
        Frame(theme, provider, look, still: false, dt, size);

    private static string? Frame(CharacterTheme theme, DisplayProvider provider, string look, bool still, double dt, double size)
    {
        if (Engine(theme) is not { } engine) return null;
        var index = Bot(theme, provider, look, still, engine);
        return engine.Tick(Advance(index, dt), size)[index]?.Svg;
    }

    /// Draws a frame into a source an Image shows; one source is redrawn for
    /// every frame of a playing tile. The frames are drawn in the drawer's
    /// 44-DIP box, scaled down for a smaller target.
    public static void Draw(CanvasImageSource target, string svg, CharacterTheme theme, double size)
    {
        using var ds = target.CreateDrawingSession(Microsoft.UI.Colors.Transparent);
        if (theme == CharacterTheme.Pixel) ds.Antialiasing = CanvasAntialiasing.Aliased;
        ds.Transform = System.Numerics.Matrix3x2.CreateScale((float)(size / Box));
        Canvas.Render(svg, ds);
    }

    public static CanvasImageSource NewTarget(double size, double scale) =>
        new(CanvasDevice.GetSharedDevice(), (float)size, (float)size, (float)(96 * scale));

    /// The look's idle frame as a still image of size DIP, for a menu icon.
    public static ImageSource? Image(CharacterTheme theme, DisplayProvider provider, string look, double size, double scale)
    {
        var key = (theme, provider, look, size, scale);
        if (Stills.TryGetValue(key, out var cached)) return cached;
        if (Still(theme, provider, look, size) is not { } svg) return null;
        var target = NewTarget(size, scale);
        Draw(target, svg, theme, size);
        return Stills[key] = target;
    }

    /// Lets go of the engines and what they drew, when Settings closes.
    public static void Release()
    {
        foreach (var engine in Engines.Values) engine?.Dispose();
        Engines.Clear();
        Bots.Clear();
        Stills.Clear();
    }
}
