using System.Diagnostics;
using InputTools;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
#if VJOY_ONE
            const int defaultId = 1;
#else
            const int defaultId = 2;
#endif
            int id = defaultId, option = Array.IndexOf(args, "--vjoy");
            if (option >= 0 && (option + 1 >= args.Length || !int.TryParse(args[option + 1], out id) || id is < 1 or > 16))
                throw new ArgumentException("--vjoyの後に1～16のIDを指定してください。");
            Application.Run(new ReaderForm(Session.FromArguments(args), id));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "DInput Reader 起動結果", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

internal sealed class ReaderForm : Form
{
    private sealed record View(DirectInputClient.Device[] Devices, DirectInputClient.Device? Device, DirectInputClient.Axis[] Axes, int ButtonCount, int PovCount, DirectInputClient.Snapshot? Sample, string Status);
    private readonly int _defaultId;
    private readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 550 };
    private readonly CheckBox _display = new() { Text = "表示更新", Checked = true, AutoSize = true };
    private readonly CheckBox _record = new() { Text = "ファイル記録", Checked = true, AutoSize = true };
    private readonly Label _health = new() { AutoSize = true };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 100 };
    private readonly CancellationTokenSource _cancel = new();
    private readonly SessionLog _log;
    private InputPanel? _panel;
    private Guid? _requested;
    private readonly object _selectionLock = new();
    private View _latest;
    private Task? _poll;
    private bool _recording = true, _closing, _changingList;
    private string _deviceSignature = "", _axisSignature = "";

    internal ReaderForm(Session session, int defaultId)
    {
        _defaultId = defaultId;
        _latest = new(Array.Empty<DirectInputClient.Device>(), null, Array.Empty<DirectInputClient.Axis>(), 0, 0, null, $"vJoy Device {_defaultId} を検索中");
        _log = new(session, $"dinput-{_defaultId}");
        _log.Write("reader-target", new { vjoyId = _defaultId });
        Text = $"DInput Reader — vJoy {_defaultId} {(_defaultId == 1 ? "通常入力・追加ボタン" : "拡張入力")}"; Size = new(1000, _defaultId == 1 ? 780 : 650); MinimumSize = new(900, 560);
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Yu Gothic UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(8) };
        layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Absolute, 36)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 65));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill }; toolbar.Controls.AddRange(new Control[] { _devices, _display, _record });
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(new Label { Text = $"測定ID: {session.MeasurementId}  版: {Session.BuildId}  既定対象: vJoy Device {_defaultId}", AutoSize = true }, 0, 1);
        layout.Controls.Add(_content, 0, 2); layout.Controls.Add(_health, 0, 3); Controls.Add(layout);
        _devices.SelectedIndexChanged += (_, _) =>
        {
            if (!_changingList && _devices.SelectedItem is Choice choice) { lock (_selectionLock) _requested = choice.Device.Instance; _log.Write("selection", choice.Device); }
        };
        _record.CheckedChanged += (_, _) => { Volatile.Write(ref _recording, _record.Checked); _log.Write("recording", new { enabled = _record.Checked }); };
        _display.CheckedChanged += (_, _) => _log.Write("display", new { enabled = _display.Checked });
        _ui.Tick += (_, _) => RefreshDisplay();
        Shown += (_, _) => { IntPtr window = Handle; _poll = Task.Run(() => PollAsync(window)); _ui.Start(); };
        FormClosing += OnClosing;
    }
    private sealed record Choice(DirectInputClient.Device Device)
    {
        public override string ToString() => $"vJoy ID {Device.VJoyId?.ToString() ?? "要選択"} | {Device.Name} | {Device.Instance}";
    }

    private async Task PollAsync(IntPtr window)
    {
        var intervals = new IntervalStatistics(); var devices = Array.Empty<DirectInputClient.Device>();
        long nextScan = 0, nextStats = 0; int errors = 0;
        try
        {
            using var resolution = new TimerResolution();
            _log.Write("timer-resolution", new { milliseconds = 1, result = resolution.Result });
            using var client = new DirectInputClient(window);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            do
            {
                long now = Stopwatch.GetTimestamp(); intervals.Observe(now);
                Guid? requested; lock (_selectionLock) requested = _requested;
                try
                {
                    if ((client.Selected is null && now >= nextScan) || (requested.HasValue && client.Selected?.Instance != requested))
                    {
                        devices = client.Enumerate(); _log.Write("device-enumeration", devices, now); nextScan = now + Stopwatch.Frequency * 2;
                        var target = requested.HasValue ? devices.FirstOrDefault(d => d.Instance == requested) : devices.FirstOrDefault(d => d.VJoyId == _defaultId);
                        if (target is not null && client.Selected?.Instance != target.Instance) { client.Open(target); _log.Write("device-open", new { device = target, axes = client.Axes, buttonCount = client.ButtonCount, povCount = client.PovCount }, now); }
                        else if (target is null) client.CloseDevice();
                    }
                    var sample = client.Selected is not null ? client.Read() : null;
                    if (sample is not null && Volatile.Read(ref _recording)) _log.Write("sample", new { instance = client.Selected!.Instance, vjoyId = client.Selected.VJoyId, result = sample.Result, axes = sample.Axes, buttons = sample.Buttons, pov = sample.Pov }, now);
                    errors = sample?.Result < 0 ? errors + 1 : 0;
                    if (errors >= 10) { _log.Write("device-recovery", new { result = sample!.Result }, now); client.CloseDevice(); nextScan = now + Stopwatch.Frequency; errors = 0; }
                    Volatile.Write(ref _latest, new(devices, client.Selected, client.Axes, client.ButtonCount, client.PovCount, sample, client.Selected is null ? "対象vJoyを接続・選択してください" : $"接続  API結果: 0x{sample?.Result ?? 0:X8}"));
                }
                catch (Exception ex) { client.CloseDevice(); nextScan = now + Stopwatch.Frequency; _log.Write("reader-error", new { error = ex.Message }, now); Volatile.Write(ref _latest, new(devices, null, Array.Empty<DirectInputClient.Axis>(), 0, 0, null, ex.Message)); }
                if (now >= nextStats) { _log.Write("poll-statistics", intervals.Snapshot(), now); nextStats = now + Stopwatch.Frequency; }
            } while (await timer.WaitForNextTickAsync(_cancel.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write("reader-error", new { error = ex.ToString() }); }
    }

    private void RefreshDisplay()
    {
        View view = Volatile.Read(ref _latest);
        _health.Text = $"{view.Status}  記録: {_log.Written}件  容量超過: {_log.Dropped}件  {_log.Error}\n{_log.Path}";
        string signature = string.Join(";", view.Devices.Select(d => $"{d.Instance}:{d.VJoyId}"));
        if (signature != _deviceSignature)
        {
            _changingList = true; _devices.Items.Clear(); foreach (var d in view.Devices) _devices.Items.Add(new Choice(d));
            _devices.SelectedItem = _devices.Items.Cast<Choice>().FirstOrDefault(c => c.Device.Instance == view.Device?.Instance) ?? _devices.Items.Cast<Choice>().FirstOrDefault(c => c.Device.VJoyId == _defaultId);
            _changingList = false; _deviceSignature = signature;
        }
        if (!_display.Checked) return;
        string axesSignature = $"{view.Device?.Instance}:{view.ButtonCount}:{view.PovCount}:" + string.Join(";", view.Axes.Select(a => a.Name));
        if (_panel is null || axesSignature != _axisSignature)
        {
            _content.Controls.Clear(); _panel?.Dispose(); _panel = new($"vJoy {view.Device?.VJoyId ?? _defaultId} — DInput読戻し", view.Axes.Select(a => a.Name), Enumerable.Range(1, view.ButtonCount).Select(i => i.ToString())); _content.Controls.Add(_panel); _axisSignature = axesSignature;
        }
        _panel.SetStatus(view.Device is null ? view.Status : $"Device {view.Device.VJoyId} | {view.Device.Instance}");
        if (view.Sample is not { Result: >= 0 } sample) return;
        for (int i = 0; i < view.Axes.Length && i < sample.Axes.Length; i++) { var axis = view.Axes[i]; _panel.Axes[axis.Name].Set(DirectInputClient.Normalize(sample.Axes[i], axis.Minimum, axis.Maximum), sample.Axes[i]); }
        for (int i = 0; i < view.ButtonCount; i++) _panel.Buttons[(i + 1).ToString()].Set(i < sample.Buttons.Length && sample.Buttons[i]);
        if (sample.Pov.Length > 0) _panel.SetPov(sample.Pov[0]);
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return; e.Cancel = true; _ui.Stop(); _cancel.Cancel(); Enabled = false;
        if (_poll is not null) await _poll;
        await _log.DisposeAsync(); _cancel.Dispose(); _ui.Dispose(); _closing = true; Close();
    }
}
