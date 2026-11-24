using System.Text;

// Optional persistent stdout/stderr for background startup tasks. Console mode
// and systemd journal output remain unchanged when --log is omitted.
public sealed class AnalyzerLog : IDisposable
{
    private readonly TextWriter stdout = Console.Out, stderr = Console.Error;
    private readonly TextWriter file;
    private AnalyzerLog(string path)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        file = TextWriter.Synchronized(new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetOut(file); Console.SetError(file);
    }
    public static AnalyzerLog? Open(string[] args)
    {
        int at = Array.IndexOf(args, "--log");
        if (at < 0) return null;
        if (at + 1 >= args.Length || string.IsNullOrWhiteSpace(args[at + 1])) throw new ArgumentException("Missing --log path");
        return new AnalyzerLog(args[at + 1]);
    }
    public void Dispose() { Console.SetOut(stdout); Console.SetError(stderr); file.Dispose(); }
}
