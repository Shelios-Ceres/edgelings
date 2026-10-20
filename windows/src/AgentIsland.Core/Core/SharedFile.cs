namespace AgentIsland.Core;

/// A read handle that tolerates the tool that writes the file still holding
/// it open. Every artifact Core reads belongs to an agent that is running
/// right now (a session log, Claude's credentials file, Grok's auth file,
/// Codex's session index), and the default File.Read* share mode throws a
/// sharing violation against an open writer, which would read as "no such
/// session" or "never signed in".
internal static class SharedFile
{
    public static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
