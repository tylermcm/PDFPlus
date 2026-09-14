using System.IO;
using System.IO.Pipes;

namespace PDFPlus.Services;

/// <summary>
/// Opening a PDF while PDFPlus is already running adds a tab to the existing window instead of
/// starting a second copy.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;

    private static string Id
    {
        get
        {
            var user = new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
            return $"PDFPlus-{user}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        }
    }

    public static bool TryClaim()
    {
        _mutex = new Mutex(true, @"Local\" + Id, out var created);
        return created;
    }

    public static bool SendToPrimary(IEnumerable<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            foreach (var path in paths) writer.WriteLine(Path.GetFullPath(path));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Listen(Action<string[]> received)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var lines = new List<string>();
                    while (reader.ReadLine() is { } line)
                        if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
                    received(lines.ToArray());
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        })
        { IsBackground = true, Name = "PDFPlus instance pipe" };
        thread.Start();
    }
}
