using System.Text;

namespace AgentIsland.Usage;

internal static class CredentialFiles
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void CreateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, OwnerReadWrite | UnixFileMode.UserExecute);
    }

    internal static void WriteNew(string path, string json) => WriteNew(path, Encoding.UTF8.GetBytes(json));

    internal static void WriteNew(string path, byte[] contents)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerReadWrite;
        using var stream = new FileStream(path, options);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }
}
