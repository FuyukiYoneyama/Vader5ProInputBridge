using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using InputTools;

// opentrack tracker-udp: little-endian double[6], X/Y/Z (cm), Yaw/Pitch/Roll (deg).
// The sender owns its socket and clock; network errors stay on this output path.
internal sealed class OpenTrackOutput : IAsyncDisposable
{
    internal sealed record Pose(double Yaw, double Pitch, double Roll);
    internal sealed record View(bool Enabled, bool InputActive, bool BiasReady, string Target,
        Pose Angles, long Sent, long Errors, string Error);
    private readonly OpenTrackConfig _config;
    private readonly SessionLog _log;
    private readonly IPEndPoint _endpoint;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _sender;
    private Pose _desired = new(0, 0, 0), _sentPose = new(0, 0, 0);
    private bool _enabled, _inputActive, _biasReady, _haveCenter;
    private double _pitchCenter, _rollCenter;
    private long _sent, _errors, _resetVersion, _recenterVersion;
    private string _error = "";

    internal OpenTrackOutput(OpenTrackConfig config, SessionLog log)
    {
        _config = config; _log = log;
        if (!IPAddress.TryParse(config.Host, out IPAddress? address) || !IPAddress.IsLoopback(address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("openTrack.hostには同じPCのIPv4ループバックアドレスを指定してください。初期値は127.0.0.1です。");
        if (config.Port is < 1 or > 65535 || config.SendHz is < 1 or > 250)
            throw new InvalidDataException("OpenTrackのポートは1～65535、送信頻度は1～250回/秒で指定してください。");
        if (!double.IsFinite(config.SmoothingMilliseconds) || config.SmoothingMilliseconds < 0)
            throw new InvalidDataException("OpenTrackの平滑化時間は有限の0以上で指定してください。");
        foreach (double value in new[] { config.YawGain, config.PitchGain, config.RollGain,
            config.YawLimitDeg, config.PitchLimitDeg, config.RollLimitDeg })
            if (!double.IsFinite(value) || value <= 0)
                throw new InvalidDataException("OpenTrackの倍率と角度上限は有限の正の値で指定してください。");
        _endpoint = new(address, config.Port); _enabled = config.Enabled;
        _sender = Task.Run(SendAsync);
        _log.Write("opentrack-configuration", new { configuration = config, format = "little-endian-double[6]: X,Y,Z,Yaw,Pitch,Roll", positionUnits = "cm", rotationUnits = "degrees" });
    }

    internal View Snapshot()
    {
        lock (_sync) return new(_enabled, _inputActive, _biasReady, _endpoint.ToString(), _sentPose, _sent, _errors, _error);
    }

    internal void SetEnabled(bool enabled)
    {
        lock (_sync) { _enabled = enabled; _resetVersion++; }
        _log.Write("opentrack-enabled", new { enabled });
    }

    internal void SetInputActive(bool active)
    {
        lock (_sync) { _inputActive = active; if (!active) _haveCenter = false; _resetVersion++; }
    }

    internal void Update(YawEstimator.State yaw, double pitch, double roll, bool resumed)
    {
        if (!double.IsFinite(yaw.AngleDeg) || !double.IsFinite(pitch) || !double.IsFinite(roll)) return;
        lock (_sync)
        {
            _inputActive = true; _biasReady = yaw.BiasReady;
            if (!_haveCenter)
            {
                _haveCenter = true; _pitchCenter = pitch; _rollCenter = roll; _resetVersion++;
            }
            if (yaw.Recentered)
            {
                _haveCenter = true; _pitchCenter = pitch; _rollCenter = roll; _resetVersion++; _recenterVersion++;
                _log.Write("opentrack-recenter", new { yaw = 0, pitch = 0, roll = 0, centerPitch = pitch, centerRoll = roll });
            }
            if (resumed || yaw.SkippedGapSeconds > 0) _resetVersion++;
            _desired = new(ConvertAngle(yaw.AngleDeg, _config.YawGain, _config.InvertYaw, _config.YawLimitDeg),
                    ConvertAngle(AngleDifference(pitch - _pitchCenter), _config.PitchGain, _config.InvertPitch, _config.PitchLimitDeg),
                    ConvertAngle(AngleDifference(roll - _rollCenter), _config.RollGain, _config.InvertRoll, _config.RollLimitDeg));
        }
    }

    private static double ConvertAngle(double angle, double gain, bool invert, double limit) =>
        Math.Clamp(angle * gain * (invert ? -1 : 1), -limit, limit);

    private static double AngleDifference(double degrees) => Math.IEEERemainder(degrees, 360);

    internal static byte[] Encode(Pose pose)
    {
        var packet = new byte[48];
        // Translation remains zero; acceleration is used for orientation correction.
        BinaryPrimitives.WriteDoubleLittleEndian(packet.AsSpan(24, 8), pose.Yaw);
        BinaryPrimitives.WriteDoubleLittleEndian(packet.AsSpan(32, 8), pose.Pitch);
        BinaryPrimitives.WriteDoubleLittleEndian(packet.AsSpan(40, 8), pose.Roll);
        return packet;
    }

    private async Task SendAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / _config.SendHz));
        UdpClient? socket = null;
        long lastTick = 0, version = -1, sentRecenter = 0, nextRetry = 0, nextLog = 0;
        Pose filtered = new(0, 0, 0);
        Pose? logged = null;
        try
        {
            while (await timer.WaitForNextTickAsync(_cancel.Token).ConfigureAwait(false))
            {
                long now = Stopwatch.GetTimestamp();
                double dt = lastTick == 0 ? 0 : (now - lastTick) / (double)Stopwatch.Frequency;
                lastTick = now;
                Pose desired; long resetVersion, recenterVersion; bool enabled, active, biasReady;
                lock (_sync)
                {
                    enabled = _enabled; active = _inputActive; biasReady = _biasReady;
                    desired = _desired; resetVersion = _resetVersion; recenterVersion = _recenterVersion;
                }
                if (!enabled || !active) { socket?.Dispose(); socket = null; version = -1; continue; }
                if (now < nextRetry) continue;
                bool recenter = recenterVersion != sentRecenter;
                if (recenter) { filtered = new(0, 0, 0); version = resetVersion; }
                else if (version != resetVersion) { filtered = desired; version = resetVersion; }
                else
                {
                    double alpha = _config.SmoothingMilliseconds == 0 ? 1
                        : 1 - Math.Exp(-dt * 1000 / _config.SmoothingMilliseconds);
                    filtered = new(filtered.Yaw + alpha * (desired.Yaw - filtered.Yaw),
                        filtered.Pitch + alpha * (desired.Pitch - filtered.Pitch),
                        filtered.Roll + alpha * (desired.Roll - filtered.Roll));
                }
                try
                {
                    socket ??= new UdpClient(AddressFamily.InterNetwork);
                    byte[] packet = Encode(filtered);
                    int bytes = await socket.SendAsync(packet, _endpoint, _cancel.Token).ConfigureAwait(false);
                    if (bytes != packet.Length) throw new IOException($"OpenTrack送信サイズ: {bytes}/48");
                    long sent; bool recovered;
                    lock (_sync)
                    {
                        sent = ++_sent; _sentPose = filtered; recovered = _error.Length > 0; _error = "";
                    }
                    sentRecenter = recenterVersion;
                    if (recovered) _log.Write("opentrack-recovered", new { target = _endpoint.ToString(), sent });
                    // Log angle changes at 0.01 degrees and a one-second heartbeat.
                    if (recenter || logged is null || Math.Abs(filtered.Yaw - logged.Yaw) >= .01
                        || Math.Abs(filtered.Pitch - logged.Pitch) >= .01 || Math.Abs(filtered.Roll - logged.Roll) >= .01 || now >= nextLog)
                    {
                        logged = filtered; nextLog = now + Stopwatch.Frequency;
                        _log.Write("opentrack-send", new { target = _endpoint.ToString(), sent, bytes, position = new double[3], angles = filtered, biasReady, recenter }, now);
                    }
                }
                catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
                {
                    long errors;
                    lock (_sync) { errors = ++_errors; _error = ex.Message; }
                    socket?.Dispose(); socket = null; nextRetry = now + Stopwatch.Frequency;
                    _log.Write("opentrack-send-error", new { error = ex.ToString(), errors, retrySeconds = 1 });
                }
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        finally { socket?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel(); await _sender.ConfigureAwait(false); _cancel.Dispose();
        _log.Write("opentrack-closed", new { sent = _sent, errors = _errors });
    }
}
