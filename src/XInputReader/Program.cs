using System.Diagnostics;
using InputTools;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try { Application.Run(new ReaderForm(Session.FromArguments(args))); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "XInput Reader 起動結果", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

internal sealed class ReaderForm : Form
{
    private sealed record Slot(uint Error, XInputClient.State State);
    private readonly InputPanel[] _panels = new InputPanel[4];
    private readonly CheckBox _display = new() { Text = "表示更新", Checked = true, AutoSize = true };
    private readonly CheckBox _record = new() { Text = "ファイル記録", Checked = true, AutoSize = true };
    private readonly Label _health = new() { AutoSize = true };
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 100 };
    private readonly CancellationTokenSource _cancel = new();
    private readonly SessionLog _log;
    private Slot[] _latest = Enumerable.Range(0, 4).Select(_ => new Slot(1167, default)).ToArray();
    private Task? _poll;
    private bool _recording = true, _closing;

    internal ReaderForm(Session session)
    {
        _log = new(session, "xinput");
        Text = "XInput Reader — 標準ゲームパッド"; Size = new(1160, 830); MinimumSize = new(900, 680);
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Yu Gothic UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(8) };
        layout.RowStyles.Add(new(SizeType.Absolute, 36)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 55));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        toolbar.Controls.AddRange(new Control[] { _display, _record, new Label { Text = $"測定ID: {session.MeasurementId}  版: {Session.BuildId}", AutoSize = true, Padding = new(8, 5, 0, 0) } });
        var panels = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        panels.ColumnStyles.Add(new(SizeType.Percent, 50)); panels.ColumnStyles.Add(new(SizeType.Percent, 50));
        panels.RowStyles.Add(new(SizeType.Percent, 50)); panels.RowStyles.Add(new(SizeType.Percent, 50));
        for (int i = 0; i < 4; i++)
        {
            _panels[i] = new($"XInput スロット {i}", new[] { "LX", "LY", "RX", "RY", "LT", "RT" }, XInputClient.Buttons.Select(b => b.Name));
            panels.Controls.Add(_panels[i], i % 2, i / 2);
        }
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(panels, 0, 1); layout.Controls.Add(_health, 0, 2); Controls.Add(layout);
        _record.CheckedChanged += (_, _) => { Volatile.Write(ref _recording, _record.Checked); _log.Write("recording", new { enabled = _record.Checked }); };
        _display.CheckedChanged += (_, _) => _log.Write("display", new { enabled = _display.Checked });
        _ui.Tick += (_, _) => RefreshDisplay();
        Shown += (_, _) => { _poll = Task.Run(PollAsync); _ui.Start(); };
        FormClosing += OnClosing;
    }

    private async Task PollAsync()
    {
        using var resolution = new TimerResolution();
        _log.Write("timer-resolution", new { milliseconds = 1, result = resolution.Result });
        var intervals = new IntervalStatistics();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        long nextStatistics = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        try
        {
            do
            {
                long now = Stopwatch.GetTimestamp(); intervals.Observe(now);
                var values = new Slot[4];
                for (uint i = 0; i < 4; i++)
                {
                    uint result = XInputClient.GetState(i, out var state);
                    if (result != 0) state = default;
                    values[i] = new(result, state);
                    if (Volatile.Read(ref _recording))
                        _log.Write("sample", new { slot = i, result, packet = state.PacketNumber, buttons = state.Gamepad.Buttons, lt = state.Gamepad.LeftTrigger, rt = state.Gamepad.RightTrigger, lx = state.Gamepad.ThumbLX, ly = state.Gamepad.ThumbLY, rx = state.Gamepad.ThumbRX, ry = state.Gamepad.ThumbRY }, now);
                }
                Volatile.Write(ref _latest, values);
                if (now >= nextStatistics) { _log.Write("poll-statistics", intervals.Snapshot(), now); nextStatistics = now + Stopwatch.Frequency; }
            } while (await timer.WaitForNextTickAsync(_cancel.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write("reader-error", new { error = ex.ToString() }); }
    }

    private void RefreshDisplay()
    {
        _health.Text = $"記録: {_log.Written}件  保存待ち: {_log.Accepted - _log.Written}件  容量超過: {_log.Dropped}件  {_log.Error}\n{_log.Path}";
        if (!_display.Checked) return;
        Slot[] values = Volatile.Read(ref _latest);
        for (int i = 0; i < 4; i++)
        {
            Slot slot = values[i]; var panel = _panels[i]; var g = slot.State.Gamepad;
            panel.SetStatus(slot.Error == 0 ? $"接続  パケット: {slot.State.PacketNumber}" : $"接続待ち  API結果: {slot.Error}");
            panel.Axes["LX"].Set(XInputClient.NormalizeStick(g.ThumbLX), g.ThumbLX); panel.Axes["LY"].Set(XInputClient.NormalizeStick(g.ThumbLY), g.ThumbLY);
            panel.Axes["RX"].Set(XInputClient.NormalizeStick(g.ThumbRX), g.ThumbRX); panel.Axes["RY"].Set(XInputClient.NormalizeStick(g.ThumbRY), g.ThumbRY);
            panel.Axes["LT"].Set(g.LeftTrigger / 127.5 - 1, g.LeftTrigger); panel.Axes["RT"].Set(g.RightTrigger / 127.5 - 1, g.RightTrigger);
            foreach (var b in XInputClient.Buttons) panel.Buttons[b.Name].Set((g.Buttons & b.Mask) != 0);
        }
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return; e.Cancel = true; _ui.Stop(); _cancel.Cancel(); Enabled = false;
        if (_poll is not null) await _poll;
        await _log.DisposeAsync(); _cancel.Dispose(); _ui.Dispose(); _closing = true; Close();
    }
}
