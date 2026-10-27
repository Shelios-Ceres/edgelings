using System.Security.Cryptography;
using System.Text;

namespace AgentIsland.Core;

/// Cursor replaces a bubble's row as the reply is written, but the row's
/// `createdAt` stays at the moment the bubble began. Measured from that alone,
/// a reply that takes longer than the quiet gap to write reads as finished
/// while it is still streaming. This notes, per conversation, when the newest
/// bubble's content was last seen to change.
///
/// A bubble seen for the first time (a new key, or the first read after the
/// app starts) has no change time and is dated by `createdAt` only: otherwise
/// a reply Cursor writes all at once would read as working for the whole gap,
/// and every old conversation would look busy at start.
internal sealed class CursorBubbleClock
{
    private readonly record struct Seen(string? Key, string Hash, DateTimeOffset? ChangedAt);

    private readonly Dictionary<string, Seen> _seen = new(StringComparer.Ordinal);

    /// The moment the conversation last moved: the later of the bubble's
    /// `createdAt` and the time this clock saw the same bubble's content change.
    public DateTimeOffset Observe(
        string conversationId, string? bubbleKey, string bubbleJson, DateTimeOffset createdAt, DateTimeOffset now)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bubbleJson)));
        DateTimeOffset? changedAt = null;
        if (_seen.TryGetValue(conversationId, out var before) && before.Key == bubbleKey)
        {
            changedAt = before.Hash == hash ? before.ChangedAt : now;
        }
        _seen[conversationId] = new Seen(bubbleKey, hash, changedAt);
        return changedAt is { } at && at > createdAt ? at : createdAt;
    }

    /// Forgets the conversations that left the read.
    public void Keep(ICollection<string> conversationIds)
    {
        foreach (var id in _seen.Keys.Where(id => !conversationIds.Contains(id)).ToList()) _seen.Remove(id);
    }
}
