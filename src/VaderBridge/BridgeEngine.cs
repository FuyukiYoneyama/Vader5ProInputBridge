using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using InputTools;

internal sealed class BridgeEngine : IAsyncDisposable
{
    internal sealed record View(string State, string Detail, long Received, long Processed, long Decoded, long Written, int Queued,
        VaderInput? Input, double Roll, double Pitch, YawEstimator.State Yaw, Dictionary<string, int> Output, Dictionary<string, double> Normalized, Dictionary<int, bool> OutputButtons,
        Dictionary<string, int> StandardOutput, Dictionary<string, double> StandardNormalized, Dictionary<int, bool> StandardButtons, uint StandardPov,
        bool Running, bool Acquired, bool OutputActive, bool ExtendedEnabled, bool ExtendedActive, OpenTrackOutput.View OpenTrack,
        double AcquireElapsedSeconds, double BiasCalibrationSeconds);
    private sealed record Packet(long Sequence, long Timestamp, int Generation, byte[] Data);
    private readonly SessionLog _log;
    private readonly BridgeConfig _config;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _commands = new(1);
    private CancellationTokenSource? _cancel;
    private Channel<Packet>? _packets;
    private Task? _supervisor, _processor;
    private HidConnection? _connection;
    private VJoyOutput? _output;
    private VJoyStandardOutput? _standardOutput;
    private bool _desiredAcquire, _useWriteFile;
    private long _received, _processed, _decoded, _written, _lastValid, _diagnosticUntil, _resumeNotBefore, _acquireRequestedAt;
    private int _queued, _generation;
    private string _state = "待機", _detail = "MI_01を開く、またはvJoy自己試験を選択してください";
    private VaderInput? _latest;
    private double _roll, _pitch;
    private readonly IntervalStatistics _intervals = new();
    private readonly VJoyOutput.TiltEstimator _tilt = new();
    private readonly YawEstimator _yaw;
    private readonly OpenTrackOutput _openTrack;
    internal SessionLog Log => _log;

    internal BridgeEngine(Session session)
    {
        _log = new(session, "bridge", enabled: false); _config = BridgeConfig.Load(Path.Combine(session.Root, "config", "bridge.json"));
        _yaw = new(_config.Yaw, _config.OpenTrack.YawLimitDeg / _config.OpenTrack.YawGain);
        _openTrack = new(_config.OpenTrack, _log);
        _log.Write("configuration", new { configuration = _config, file = Session.FileIdentity(Program.ConfigPath) });
    }
    internal View Snapshot()
    {
        lock (_sync) return new(_state, _detail, Interlocked.Read(ref _received), Interlocked.Read(ref _processed), Interlocked.Read(ref _decoded), Interlocked.Read(ref _written), Volatile.Read(ref _queued), _latest, _roll, _pitch, _yaw.Snapshot,
            _output?.AxisValues ?? new(), _output?.NormalizedAxes ?? new(), _output?.ButtonValues ?? new(),
            _standardOutput?.AxisValues ?? new(), _standardOutput?.NormalizedAxes ?? new(), _standardOutput?.ButtonValues ?? new(), _standardOutput?.Pov ?? uint.MaxValue,
            _cancel is not null, Volatile.Read(ref _desiredAcquire), _output is not null || _standardOutput is not null,
            _config.ExtendedEnabled, _output is not null, _openTrack.Snapshot(),
            _acquireRequestedAt == 0 ? 0 : (Stopwatch.GetTimestamp() - _acquireRequestedAt) / (double)Stopwatch.Frequency,
            _config.Yaw.BiasCalibrationSeconds);
    }
    internal void SetOpenTrackEnabled(bool enabled) => _openTrack.SetEnabled(enabled);
    internal async Task SetLoggingEnabledAsync(bool enabled)
    {
        await _log.SetEnabledAsync(enabled).ConfigureAwait(false);
        if (enabled)
        {
            _log.Write("configuration", new { configuration = _config, file = Session.FileIdentity(Program.ConfigPath) });
            _log.Write("capture-context", Snapshot());
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (module.ModuleName.Equals("vJoyInterface.dll", StringComparison.OrdinalIgnoreCase))
                    _log.Write("native-library-loaded", Session.FileIdentity(module.FileName));
        }
    }
    private void State(string state, string detail)
    {
        lock (_sync) { if (_state == state && _detail == detail) return; _state = state; _detail = detail; }
        _log.Write("state", new { state, detail, outputPolicy = state is "回復中" or "入力回復中" or "機器設定待ち" ? "hold-last" : "continue" });
    }
    internal async Task OpenAsync(bool useWriteFile)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cancel is not null) return;
            _useWriteFile = useWriteFile; _desiredAcquire = false;
            _received = _processed = _decoded = _written = _lastValid = 0; _queued = 0; _latest = null; _tilt.Reset();
            Interlocked.Exchange(ref _resumeNotBefore, 0);
            Interlocked.Exchange(ref _acquireRequestedAt, 0);
            _yaw.ResumeClock();
            _openTrack.SetInputActive(false);
            _cancel = new();
            _packets = Channel.CreateBounded<Packet>(new BoundedChannelOptions(4096) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            _processor = Task.Run(() => ProcessAsync(_packets.Reader));
            _supervisor = Task.Run(() => SuperviseAsync(_packets.Writer, _cancel.Token));
            State("検索中", "VID_37D7の全PIDを列挙しています");
        }
        finally { _commands.Release(); }
    }
    internal void RequestAcquire()
    {
        Interlocked.CompareExchange(ref _acquireRequestedAt, Stopwatch.GetTimestamp(), 0);
        Volatile.Write(ref _desiredAcquire, true);
        _log.Write("acquire-requested", new { api = _useWriteFile ? "WriteFile" : "HidD_SetOutputReport" });
    }
    internal async Task StartOutputAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_output is not null || _standardOutput is not null) return;
                if (!StartOutputs()) State("出力待ち", "有効なvJoy出力先の設定・所有状態を確認してください");
            }
        }
        finally { _commands.Release(); }
    }
    // Called under _sync. Acquire the enabled devices before publishing their outputs.
    private bool StartOutputs()
    {
        if (_config.ExtendedEnabled && _config.Standard.Enabled && _config.Standard.VJoyId == _config.VJoyId)
            throw new InvalidDataException("通常入力と拡張入力には別々のvJoy IDを設定してください。");
        VJoyOutput? output = _config.ExtendedEnabled ? new(_config, message => _log.Write("vjoy-api", new { message })) : null;
        VJoyStandardOutput? standard = _config.Standard.Enabled ? new(_config.Standard, message => _log.Write("vjoy-standard-api", new { message })) : null;
        bool published = false;
        try
        {
            if (output is null && standard is null) return false;
            if ((output is not null && !output.Start()) || (standard is not null && !standard.Start())) return false;
            _output = output; _standardOutput = standard; published = true;
            _log.Write("vjoy-start", new { device = output is null ? (int?)null : _config.VJoyId, extendedEnabled = _config.ExtendedEnabled,
                standardDevice = standard is null ? (int?)null : _config.Standard.VJoyId, standardButtons = _config.Standard.Buttons });
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (module.ModuleName.Equals("vJoyInterface.dll", StringComparison.OrdinalIgnoreCase))
                    _log.Write("native-library-loaded", Session.FileIdentity(module.FileName));
            return true;
        }
        finally { if (!published) { try { standard?.Dispose(); } finally { output?.Dispose(); } } }
    }
    internal void Diagnostic()
    {
        Interlocked.Exchange(ref _diagnosticUntil, Stopwatch.GetTimestamp() + 60 * Stopwatch.Frequency);
        _log.Write("diagnostic-start", new { durationSeconds = 60 });
    }
    private async Task SuperviseAsync(ChannelWriter<Packet> writer, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                HidConnection? connection = null;
                try
                {
                    var devices = HidConnection.Enumerate(); _log.Write("hid-enumeration", devices);
                    var candidates = devices.Where(HidConnection.Matches).ToArray();
                    if (candidates.Length != 1) throw new IOException($"MI_01候補: {candidates.Length}件。接続・識別値を確認してください。");
                    connection = new(candidates[0], _useWriteFile, _log);
                    lock (_sync) { _connection = connection; Interlocked.Exchange(ref _resumeNotBefore, Stopwatch.GetTimestamp()); }
                    int generation = ++_generation; long nextRequest = 0, nextStats = 0;
                    bool requested = false, recovering = false; int attempts = 0;
                    long requestedAt = 0, lastRequest = 0, lastInput = 0;
                    long configurationStarted = 0, lastConfiguration = 0;
                    bool configurationPending = false, statusPending = false;
                    State("オープン", candidates[0].Path);
                    var buffer = new byte[connection.Target.InputLength];
                    while (!token.IsCancellationRequested)
                    {
                        long now = Stopwatch.GetTimestamp();
                        bool configurationBusy = configurationPending && now - lastConfiguration < Stopwatch.Frequency;
                        long inputAnchor = Math.Max(requestedAt, lastInput);
                        if (lastConfiguration != 0) inputAnchor = Math.Max(inputAnchor, lastConfiguration + Stopwatch.Frequency);
                        long inputAge = requested ? Math.Max(0, now - inputAnchor) : 0;
                        bool staleInput = requested && inputAge >= Stopwatch.Frequency / 2;
                        if (statusPending && !configurationBusy)
                        {
                            connection.QueryStatus(); statusPending = false;
                        }
                        if (Volatile.Read(ref _desiredAcquire) && !configurationBusy &&
                            (!requested || (configurationPending && now - lastRequest >= Stopwatch.Frequency / 2) || now >= nextRequest || (staleInput && now - lastRequest >= Stopwatch.Frequency / 2)))
                        {
                            if (!requested) requestedAt = now;
                            string reason = configurationPending ? "configuration-recovery" : !requested ? "initial" : staleInput ? "input-recovery" : "heartbeat";
                            bool queryInfo = reason is "initial" or "heartbeat" or "configuration-recovery";
                            bool success = connection.Acquire(queryInfo); requested = true; attempts++;
                            lastRequest = Stopwatch.GetTimestamp(); nextRequest = lastRequest + Stopwatch.Frequency * 30;
                            _log.Write("acquire-attempt", new { generation, reason, attempt = attempts, success, queryInfo,
                                inputAgeMilliseconds = inputAge * 1000d / Stopwatch.Frequency });
                            if (configurationPending && success)
                            {
                                lock (_sync) Interlocked.Exchange(ref _resumeNotBefore, lastRequest);
                                _log.Write("configuration-resume", new { generation, attempt = attempts,
                                    elapsedMilliseconds = (lastRequest - configurationStarted) * 1000d / Stopwatch.Frequency,
                                    quietMilliseconds = (lastRequest - lastConfiguration) * 1000d / Stopwatch.Frequency,
                                    policy = "hold-angles-resume-clock" }, lastRequest);
                                configurationPending = false; recovering = true;
                                State("入力回復中", "機器設定の通信が落ち着いたため、同じ接続で取得を再開しました / 最終値を保持");
                            }
                            else if (staleInput)
                            {
                                recovering = true;
                                State("入力回復中", $"最終値を保持 / 同じ接続で取得要求{attempts}回目");
                            }
                            else if (lastInput == 0)
                                State(success ? "受信待ち" : "取得要求を再試行", $"要求{attempts}回 / {_useWriteFile switch { true => "WriteFile", false => "HidD_SetOutputReport" }}");
                        }
                        if (requested && !configurationBusy && inputAge > 5 * Stopwatch.Frequency)
                            throw new TimeoutException("同じ接続で取得を再試行し、正常なMI_01入力を5秒待ちました。最後のvJoy値を保持して接続を開き直します。");
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(100);
                        int count;
                        try { count = await connection.Input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                        if (count == 0) throw new EndOfStreamException("MI_01読取りが0バイトで終了しました。");
                        long timestamp = Stopwatch.GetTimestamp(); _intervals.Observe(timestamp);
                        long sequence = Interlocked.Increment(ref _received);
                        var packet = new Packet(sequence, timestamp, generation, buffer.AsSpan(0, count).ToArray());
                        if (VaderReportDecoder.TryGetCommand(packet.Data, out int offset, out byte command))
                        {
                            if (command == 0xEF && count - offset >= 31)
                            {
                                if (!configurationPending && (lastInput == 0 || recovering))
                                    _log.Write("input-resumed", new { sequence, generation, attempts,
                                        gapMilliseconds = (timestamp - Math.Max(requestedAt, lastInput)) * 1000d / Stopwatch.Frequency,
                                        withinSameConnection = lastInput != 0 }, timestamp);
                                lastInput = timestamp;
                                if (!configurationPending) recovering = false;
                            }
                            else
                            {
                                bool? available = command == 0x10 && count - offset >= 10 ? packet.Data[offset + 9] == 1 : null;
                                bool? rejected = command == 0x1C && count - offset >= 7
                                    ? packet.Data[offset + 5] != 1 && packet.Data[offset + 6] == 0 : null;
                                _log.Write("hid-control", new { sequence, generation, command = $"{command:X2}", available,
                                    acquireRejected = rejected, raw = Convert.ToHexString(packet.Data) }, timestamp);
                                if (command is 0x02 or 0x04 or 0x07 or 0xA1 or 0xA2 or 0xA3 or 0xA7 or 0xAC)
                                {
                                    if (!configurationPending)
                                    {
                                        configurationStarted = timestamp;
                                        _log.Write("configuration-traffic", new { sequence, generation, command = $"{command:X2}",
                                            quietSeconds = 1, policy = "hold-last" }, timestamp);
                                    }
                                    lastConfiguration = timestamp; configurationPending = true; recovering = true;
                                    lock (_sync) Interlocked.Exchange(ref _resumeNotBefore, long.MaxValue);
                                    State("機器設定待ち", "設定応答を受信中 / 最終値を保持 / 通信が1秒落ち着いてから取得を再開");
                                }
                                if (command == 0x11) statusPending = true;
                            }
                        }
                        Interlocked.Increment(ref _queued);
                        if (!writer.TryWrite(packet)) { _log.Write("queue-wait", new { sequence, capacity = 4096 }); await writer.WriteAsync(packet).ConfigureAwait(false); }
                        if (timestamp >= nextStats)
                        {
                            _log.Write("receive-statistics", new { received = Interlocked.Read(ref _received), processed = Interlocked.Read(ref _processed), decoded = Interlocked.Read(ref _decoded), written = Interlocked.Read(ref _written), queued = Volatile.Read(ref _queued), interval = _intervals.Snapshot(), thread = Environment.CurrentManagedThreadId }, timestamp);
                            nextStats = timestamp + Stopwatch.Frequency;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    var held = Snapshot();
                    _log.Write("read-recovery", new { error = ex.ToString(), win32Error = ex is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : 0, heldOutput = held.Output, heldStandard = held.StandardOutput, heldStandardButtons = held.StandardButtons, heldStandardPov = held.StandardPov });
                    State("回復中", ex.Message);
                }
                finally
                {
                    if (connection is not null)
                    {
                        try { connection.Release(); } catch (Exception ex) { _log.Write("release-error", new { error = ex.ToString() }); }
                        lock (_sync) _connection = null;
                        connection.Dispose();
                    }
                }
                if (!token.IsCancellationRequested) await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { State("回復処理の結果を確認", ex.Message); _log.Write("supervisor-error", new { error = ex.ToString() }); }
        finally { writer.TryComplete(); _log.Write("read-loop-ended", new { reason = token.IsCancellationRequested ? "normal-stop" : "supervisor-error" }); }
    }
    private async Task ProcessAsync(ChannelReader<Packet> reader)
    {
        int generation = 0; long previousTimestamp = 0; bool[] previous = new bool[20];
        await foreach (var packet in reader.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _queued); bool decoded = VaderReportDecoder.TryDecode(packet.Data, out VaderInput? input);
            Interlocked.Increment(ref _processed);
            // 制御応答と未知のレポートは常時保存し、入力の生データは診断期間に加える。
            _log.Write("report-processed", new { sequence = packet.Sequence, generation = packet.Generation, decoded, length = packet.Data.Length, raw = !decoded || packet.Timestamp <= Interlocked.Read(ref _diagnosticUntil) ? Convert.ToHexString(packet.Data) : null }, packet.Timestamp);
            if (!decoded || input is null) continue;
            Interlocked.Increment(ref _decoded); Interlocked.Exchange(ref _lastValid, packet.Timestamp);
            bool wrote = false, standardWrote = false; string? error = null, standardError = null;
            bool biasBecameReady;
            lock (_sync)
            {
                _latest = input;
                if (packet.Timestamp < Interlocked.Read(ref _resumeNotBefore))
                {
                    _log.Write("report-held", new { sequence = packet.Sequence, generation = packet.Generation,
                        reason = "configuration-or-connection-recovery", policy = "hold-last" }, packet.Timestamp);
                    continue;
                }
                bool gap = previousTimestamp != 0 && packet.Timestamp - previousTimestamp > _config.Yaw.MaxSampleGapSeconds * Stopwatch.Frequency;
                bool resumed = generation != packet.Generation || gap; generation = packet.Generation;
                if (gap) _log.Write("input-sample-gap", new { sequence = packet.Sequence, generation,
                    gapSeconds = (packet.Timestamp - previousTimestamp) / (double)Stopwatch.Frequency,
                    policy = "hold-angles-resume-clock" }, packet.Timestamp);
                previousTimestamp = packet.Timestamp;
                for (int i = 0; i < 20; i++) if (previous[i] != input.Buttons[i]) _log.Write("button", new { sequence = packet.Sequence, button = i, down = input.Buttons[i] }, packet.Timestamp);
                previous = input.Buttons;
                if (resumed) { _tilt.ResumeClock(); _yaw.ResumeClock(); }
                bool biasWasReady = _yaw.Snapshot.BiasReady;
                _yaw.Update(input.Gyro, input.Accel, input.Buttons[5], packet.Timestamp);
                biasBecameReady = !biasWasReady && _yaw.Snapshot.BiasReady;
                _tilt.Update(input.Gyro, input.Accel, _config.Tilt.CorrectionTimeConstantSeconds, packet.Timestamp);
                _roll = _tilt.RollDegrees; _pitch = _tilt.PitchDegrees;
                _openTrack.Update(_yaw.Snapshot, _pitch, _roll, resumed);
                if (_output is not null)
                {
                    try
                    {
                        if (resumed) _output.ResumeClock();
                        _output.SetSampleTime(packet.Timestamp); _output.WriteButtonStates(input.Buttons); _output.WriteGyro(input.Gyro); _output.WriteTiltDegrees(_roll, _pitch);
                        _output.WriteAccel(input.Accel); _output.WriteYawDegrees(_yaw.Snapshot.AngleDeg);
                        wrote = true;
                    }
                    catch (Exception ex) { error = ex.Message; _log.Write("vjoy-write-error", new { sequence = packet.Sequence, error }); }
                }
                if (_standardOutput is not null)
                {
                    try { _standardOutput.Write(input); standardWrote = true; }
                    catch (Exception ex) { standardError = ex.Message; _log.Write("vjoy-standard-write-error", new { sequence = packet.Sequence, error = standardError }); }
                }
                if ((_output is not null || _standardOutput is not null) && (_output is null || wrote) && (_standardOutput is null || standardWrote))
                    Interlocked.Increment(ref _written);
            }
            if (_log.Enabled)
            {
                var view = Snapshot();
                if (biasBecameReady) _log.Write("yaw-bias-ready", new { sequence = packet.Sequence, axis = _config.Yaw.SensorAxis, invert = _config.Yaw.Invert,
                    acquireElapsedSeconds = view.AcquireElapsedSeconds, startupTargetSeconds = 5, yaw = view.Yaw }, packet.Timestamp);
                if (view.Yaw.Recentered) _log.Write("yaw-recenter", new { sequence = packet.Sequence, yaw = view.Yaw,
                    standardButton = _config.Standard.Buttons["Guide"], standardSuccess = standardWrote, openTrack = view.OpenTrack }, packet.Timestamp);
                if (view.Yaw.SkippedGapSeconds > 0) _log.Write("yaw-sample-gap", new { sequence = packet.Sequence, yaw = view.Yaw, policy = "hold-angle-resume-clock" }, packet.Timestamp);
                _log.Write("report-output", new { sequence = packet.Sequence, vjoyId = _config.VJoyId, enabled = view.ExtendedEnabled, active = view.ExtendedActive, success = wrote, error, axes = view.Output, buttons = view.OutputButtons,
                    standardInput = input.Standard, standardInputButtons = input.Buttons.Take(11).ToArray(), standardVjoyId = _config.Standard.VJoyId, standardSuccess = standardWrote, standardError,
                    standardAxes = view.StandardOutput, standardButtons = view.StandardButtons, standardPov = view.StandardPov,
                    yaw = view.Yaw, openTrack = view.OpenTrack, receivedTicks = packet.Timestamp, writeCompletedTicks = Stopwatch.GetTimestamp() }, packet.Timestamp);
                if (packet.Timestamp <= Interlocked.Read(ref _diagnosticUntil)) _log.Write("input-sample", new { sequence = packet.Sequence, standard = input.Standard, buttons = input.Buttons, gyro = input.Gyro, accel = input.Accel, roll = _roll, pitch = _pitch, yaw = view.Yaw }, packet.Timestamp);
            }
            string? outputError = error ?? standardError;
            lock (_sync)
                if (packet.Timestamp >= Interlocked.Read(ref _resumeNotBefore))
                    State(outputError is null ? "受信中" : "出力を再試行", outputError ?? "MI_01を順番に処理しています");
        }
        _log.Write("processing-drained", new { received = _received, processed = _processed, decoded = _decoded, written = _written, queued = _queued });
    }
    internal async Task StopAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _commands.Release(); }
    }
    private async Task StopCoreAsync()
    {
        _cancel?.Cancel();
        if (_supervisor is not null) await _supervisor.ConfigureAwait(false);
        if (_processor is not null) await _processor.ConfigureAwait(false);
        lock (_sync) { _openTrack.SetInputActive(false); StopOutputs(); _yaw.ResumeClock(); }
        _cancel?.Dispose(); _cancel = null; _supervisor = _processor = null; _desiredAcquire = false;
        Interlocked.Exchange(ref _acquireRequestedAt, 0);
        State("停止", "出力先のボタン・十字キー解放、軸休止、機器取得の解除を完了しました");
    }
    private void StopOutputs()
    {
        try { _standardOutput?.Dispose(); }
        finally { _standardOutput = null; try { _output?.Dispose(); } finally { _output = null; } }
    }
    internal async Task SelfTestAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        if (_cancel is not null || _output is not null || _standardOutput is not null) { _commands.Release(); throw new InvalidOperationException("停止・解除後に自己試験を開始してください。"); }
        try
        {
            lock (_sync)
            {
                if (!StartOutputs()) { State("出力待ち", "有効なvJoy出力先の設定・所有状態を確認してください"); return; }
            }
            State("自己試験", "有効な出力先の軸・割当ボタン・十字キー8方向を出力します");
            foreach (double value in new[] { -1d, 0, 1, 0 })
            {
                lock (_sync) { _output?.SelfTestAxes(value); _standardOutput?.SelfTestAxes(value); }
                _log.Write("self-test-axes", new { normalized = value }); await Task.Delay(500).ConfigureAwait(false);
            }
            var buttons = (_output is null ? Array.Empty<int>() : _config.Buttons.Values.AsEnumerable())
                .Concat(_standardOutput is null ? Array.Empty<int>() : _config.Standard.Buttons.Values.AsEnumerable()).Distinct().Order();
            foreach (int number in buttons)
            {
                byte button = checked((byte)number);
                lock (_sync)
                {
                    if (_config.Buttons.ContainsValue(number)) _output?.SelfTestButton(button, true);
                    if (_config.Standard.Buttons.ContainsValue(number)) _standardOutput?.SelfTestButton(button, true);
                }
                await Task.Delay(300).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_config.Buttons.ContainsValue(number)) _output?.SelfTestButton(button, false);
                    if (_config.Standard.Buttons.ContainsValue(number)) _standardOutput?.SelfTestButton(button, false);
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
            if (_standardOutput is not null)
                foreach (uint pov in new uint[] { 0, 4500, 9000, 13500, 18000, 22500, 27000, 31500, uint.MaxValue })
                { lock (_sync) _standardOutput.SelfTestPov(pov); await Task.Delay(500).ConfigureAwait(false); }
        }
        finally { try { lock (_sync) StopOutputs(); State("停止", "vJoy自己試験を終了しました"); } finally { _commands.Release(); } }
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            try { await _openTrack.DisposeAsync().ConfigureAwait(false); }
            finally { await _log.DisposeAsync().ConfigureAwait(false); _commands.Dispose(); }
        }
    }
}
