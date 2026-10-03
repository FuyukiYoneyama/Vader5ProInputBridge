using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace InputTools;

public sealed record Session(string MeasurementId, string Root, string LogDirectory)
{
    public static Session FromArguments(string[] args)
    {
        string root = FindRoot();
        string id = Argument(args, "--measurement") ?? DefaultMeasurement(root);
        if (id.Length > 80 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("測定IDは英数字・ハイフン・下線で指定してください。");
        string logDirectory = Path.GetFullPath(Argument(args, "--log-dir") ?? Path.Combine(root, "logs", id));
        return new(id, root, logDirectory);
    }

    public static string? Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string DefaultMeasurement(string root)
    {
        string directory = System.IO.Path.Combine(root, "config"); Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "measurement-id.txt");
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        try { File.Move(temporary, path, false); }
        catch (IOException) when (File.Exists(path)) { }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return File.ReadAllText(path).Trim();
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "config", "bridge.json"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    public static string BuildId => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";
    public static object FileIdentity(string path) => File.Exists(path)
        ? new { path = Path.GetFullPath(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "" }
        : new { path = Path.GetFullPath(path), sha256 = "", version = "" };
}

public sealed class SessionLog : IAsyncDisposable
{
    private readonly Session _session;
    private readonly string _source;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _switch = new(1);
    private Capture? _capture, _last;
    public bool Enabled => Volatile.Read(ref _capture) is not null;
    private Capture? Current => Volatile.Read(ref _capture) ?? Volatile.Read(ref _last);
    public string Path => Current?.Path ?? "";
    public long Accepted => Current?.Accepted ?? 0;
    public long Written => Current?.Written ?? 0;
    public long Dropped => Current?.Dropped ?? 0;
    public string? Error => Current?.Error;

    public SessionLog(Session session, string source, bool enabled = true)
    {
        _session = session; _source = source;
        if (enabled) StartCapture();
    }

    // Capture allocates the writer and file resources when explicitly enabled.
    private void StartCapture()
    {
        var capture = new Capture(_session, _source);
        capture.Write("session-start", new
        {
            buildId = Session.BuildId, processId = Environment.ProcessId, user = Environment.UserName,
            executable = Environment.ProcessPath, stopwatchFrequency = Stopwatch.Frequency,
            assembly = Session.FileIdentity(Assembly.GetEntryAssembly()!.Location)
        });
        _capture = capture;
    }

    public void Write(string kind, object data, long? timestamp = null)
    {
        if (!Enabled) return;
        lock (_sync) _capture?.Write(kind, data, timestamp);
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            Capture? stopping = null;
            lock (_sync)
            {
                if (enabled)
                {
                    if (_capture is null) StartCapture();
                }
                else if (_capture is { } capture)
                {
                    capture.Write("session-end", new { accepted = capture.Accepted, written = capture.Written, dropped = capture.Dropped, error = capture.Error });
                    stopping = capture; _last = capture; _capture = null;
                }
            }
            if (stopping is not null) await stopping.CompleteAsync().ConfigureAwait(false);
        }
        finally { _switch.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await SetEnabledAsync(false).ConfigureAwait(false);
        _switch.Dispose();
    }

    private sealed class Capture
    {
        private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(65536) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Task _writer;
        private readonly Session _session;
        private readonly string _source;
        private long _accepted, _written, _dropped;
        private string? _error;
        internal string Path { get; }
        internal long Accepted => Interlocked.Read(ref _accepted);
        internal long Written => Interlocked.Read(ref _written);
        internal long Dropped => Interlocked.Read(ref _dropped);
        internal string? Error => Volatile.Read(ref _error);
        internal Capture(Session session, string source)
        {
            _session = session; _source = source;
            Directory.CreateDirectory(session.LogDirectory);
            Path = System.IO.Path.Combine(session.LogDirectory, $"{source}-{DateTime.UtcNow:HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
            _writer = Task.Run(WriteAsync);
        }
        internal void Write(string kind, object data, long? timestamp = null)
        {
            string line = JsonSerializer.Serialize(new { utc = DateTime.UtcNow, ticks = timestamp ?? Stopwatch.GetTimestamp(), measurementId = _session.MeasurementId, buildId = Session.BuildId, source = _source, kind, data });
            if (_lines.Writer.TryWrite(line)) Interlocked.Increment(ref _accepted);
            else Interlocked.Increment(ref _dropped);
        }
        private async Task WriteAsync()
        {
            try
            {
                await using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                long nextFlush = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                await foreach (string line in _lines.Reader.ReadAllAsync())
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    Interlocked.Increment(ref _written);
                    if (Stopwatch.GetTimestamp() >= nextFlush)
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                        nextFlush = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    }
                }
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Volatile.Write(ref _error, $"{ex.GetType().Name}: {ex.Message}"); _lines.Writer.TryComplete(ex); }
        }
        internal async Task CompleteAsync()
        {
            _lines.Writer.TryComplete();
            await _writer.ConfigureAwait(false);
        }
    }
}

public sealed class IntervalStatistics
{
    private readonly Queue<double> _intervals = new();
    private long _previous;
    public void Observe(long timestamp)
    {
        if (_previous != 0)
        {
            _intervals.Enqueue((timestamp - _previous) * 1000.0 / Stopwatch.Frequency);
            if (_intervals.Count > 6000) _intervals.Dequeue();
        }
        _previous = timestamp;
    }
    public object Snapshot()
    {
        double[] values = _intervals.Order().ToArray();
        return new { count = values.Length, medianMs = At(values, .5), p95Ms = At(values, .95), maximumMs = At(values, 1) };
    }
    private static double At(double[] values, double fraction) => values.Length == 0 ? 0 : values[Math.Clamp((int)Math.Ceiling(values.Length * fraction) - 1, 0, values.Length - 1)];
}
