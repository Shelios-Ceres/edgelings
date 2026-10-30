using AgentIsland.Bridge;

// A2: the same requests over stdin and stdout, one JSON object per line. The
// process ends when stdin closes.
var stdin = Console.In;
var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
while (stdin.ReadLine() is { } line)
{
    if (line.Length == 0) continue;
    stdout.WriteLine(BridgeApi.Handle(line));
}
