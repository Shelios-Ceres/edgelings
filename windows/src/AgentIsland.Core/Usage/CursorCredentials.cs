using System.Text.Json;
using AgentIsland.Core;
using Microsoft.Data.Sqlite;

namespace AgentIsland.Usage;

public static class CursorCredentials
{
    public sealed record Session(string AccessToken, string UserId);
    internal sealed record Snapshot(Session? Session, string? Plan, string? Email);

    public static string StateDbPath => Path.Combine(
        IslandPaths.RoamingAppData, "Cursor", "User", "globalStorage", "state.vscdb");

    public static bool Exists() => File.Exists(StateDbPath);

    internal static Snapshot? ReadSnapshot() =>
        File.Exists(StateDbPath) ? ReadDatabase(StateDbPath) : null;

    internal static Snapshot ReadDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // One statement gives all identity fields the same committed WAL snapshot.
        command.CommandText = "SELECT key, value FROM ItemTable WHERE key IN "
            + "('cursorAuth/accessToken', 'cursorAuth/stripeMembershipType', 'cursorAuth/cachedEmail')";
        using var reader = command.ExecuteReader();
        string? token = null, plan = null, email = null;
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            var value = reader.GetString(1);
            switch (reader.GetString(0))
            {
                case "cursorAuth/accessToken": token = value; break;
                case "cursorAuth/stripeMembershipType": plan = NormalizePlan(value); break;
                case "cursorAuth/cachedEmail": email = string.IsNullOrWhiteSpace(value) ? null : value; break;
            }
        }
        var session = token is not null && LooksLikeJwt(token) && DecodeSubject(token) is { } subject
            ? new Session(token, subject) : null;
        return new Snapshot(session, plan, email);
    }
    // MARK: - Pure helpers (unit-tested)

    public static bool LooksLikeJwt(string token) =>
        token.StartsWith("eyJ", StringComparison.Ordinal) && token.Split('.').Length == 3;

    /// The JWT's `sub` claim, e.g. "user_01ABC…". The signature is irrelevant
    /// here: the token is only ever handed back to Cursor, which verifies it.
    /// Payload is base64url with the padding stripped.
    public static string? DecodeSubject(string token)
    {
        if (DecodePayload(token) is not { } payload) return null;
        var subject = Jsonl.GetString(payload, "sub");
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    /// Cursor's known tiers, longest-match-first so "pro_plus" never reads as
    /// "pro". Unknown strings drop rather than show garbage in a chip.
    public static string? NormalizePlan(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var lower = raw.ToLowerInvariant();
        foreach (var tier in Tiers)
        {
            if (lower.StartsWith(tier, StringComparison.Ordinal)) return tier.Replace('_', ' ');
        }
        return null;
    }

    private static readonly string[] Tiers =
    {
        "free_trial", "pro_plus", "enterprise", "business", "ultra", "team", "free", "pro",
    };

    private static JsonElement? DecodePayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            // The document is disposed on return; Clone detaches the element.
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
