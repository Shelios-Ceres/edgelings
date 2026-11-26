using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentIsland.Usage;

/// What the Grok billing request says it is: the platform and architecture it
/// runs on, and the version of the grok CLI installed here. Read from what is
/// on disk (never by running grok), so the request is no stranger than the
/// CLI the user already has. A version that cannot be read is left out
/// rather than invented.
internal readonly record struct GrokClientIdentity(string? Version, string Platform, string Architecture)
{
    private static readonly Regex VersionShape = new(@"^(\d+\.\d+\.\d+)(?:\.\d+)?([-+][0-9A-Za-z.]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex ChangelogHeading = new(@"^#\s+v?(\S+)", RegexOptions.CultureInvariant);

    public string UserAgent => Version is { } version
        ? $"grok-pager/{version} grok-shell/{version} ({Platform}; {Architecture})"
        : $"grok-pager grok-shell ({Platform}; {Architecture})";

    /// `home` is the grok home folder, the one holding auth.json.
    public static GrokClientIdentity Current(string home) => new(InstalledVersion(home), CurrentPlatform, CurrentArchitecture);

    internal static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// The machine's architecture in the CLI's own spelling, not the process's:
    /// a universal build under Rosetta still sits next to a native grok.
    internal static string CurrentArchitecture => ArchitectureName(RuntimeInformation.OSArchitecture);

    internal static string ArchitectureName(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// The exe's own version resource where it has one (Windows; the CLI's
    /// build carries none today). Otherwise the version.json the CLI keeps in
    /// its home and the heading of the CHANGELOG.md it unpacks there: either
    /// alone could be the newest version seen rather than the installed one
    /// (a newer build may be downloading), so the two must agree. Null when
    /// nothing reads as a version; the request then says none.
    internal static string? InstalledVersion(string home)
    {
        if (FromExe(Path.Combine(home, "bin", "grok.exe")) is { } exe) return exe;
        return FromVersionJson(Path.Combine(home, "version.json")) is { } stored
            && FromChangelog(Path.Combine(home, "CHANGELOG.md")) is { } heading
            && stored == heading ? stored : null;
    }

    internal static string? FromExe(string exe)
    {
        try
        {
            return OperatingSystem.IsWindows() && File.Exists(exe) ? Valid(FileVersionInfo.GetVersionInfo(exe).ProductVersion) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FromVersionJson(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("version", out var node) && node.ValueKind == JsonValueKind.String
                ? Valid(node.GetString()) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? FromChangelog(string file)
    {
        try
        {
            var heading = File.ReadLines(file).FirstOrDefault(line => line.Trim().Length > 0);
            return heading is not null && ChangelogHeading.Match(heading) is { Success: true } match ? Valid(match.Groups[1].Value) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// The first three parts (1.0.70.0 is 1.0.70) and any pre-release suffix, or null.
    private static string? Valid(string? text) =>
        text is not null && VersionShape.Match(text.Trim()) is { Success: true } match ? match.Groups[1].Value + match.Groups[2].Value : null;
}
