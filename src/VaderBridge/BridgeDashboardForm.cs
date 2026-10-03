using System.Globalization;
using InputTools;

internal sealed class BridgeDashboardForm : Form
{
    private static readonly (string Name, int Index)[] Buttons = { ("M1", 11), ("M2", 12), ("M3", 13), ("M4", 14), ("LM", 17), ("RM", 18), ("C", 15), ("Z", 16), ("Fn", 19), ("HOME", 5) };
    private readonly BridgeEngine _engine;
    private readonly Session _session;
    private ResidentSettings _settings;
    private readonly NotifyIcon _tray = new();
    private readonly TrayImages _images = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _trayStatus = new("開始待ち") { Enabled = false };
    private readonly ToolStripMenuItem _trayPause = new("一時停止");
    private readonly ToolStripMenuItem _trayLogging = new("ログ採取開始");
    private readonly Button _resume = new() { Text = "開始／再開", AutoSize = true };
    private readonly Button _pause = new() { Text = "一時停止", AutoSize = true };
    private readonly Button _logToggle = new() { Text = "ログ採取開始", AutoSize = true };
    private readonly Button _settingsButton = new() { Text = "設定", AutoSize = true };
    private readonly Button _exit = new() { Text = "終了", AutoSize = true };
    private readonly CheckBox _display = new() { Text = "入力表示ON", Checked = true, AutoSize = true };
    private readonly CheckBox _openTrack = new() { Text = "OpenTrack出力ON", AutoSize = true };
    private readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Button _open = new() { Text = "1 MI_01を開く", AutoSize = true };
    private readonly Button _acquire = new() { Text = "2 取得要求", AutoSize = true };
    private readonly Button _output = new() { Text = "3 vJoy出力開始", AutoSize = true };
    private readonly Button _stop = new() { Text = "停止・解除", AutoSize = true };
    private readonly Button _test = new() { Text = "vJoy自己試験", AutoSize = true };
    private readonly Button _trace = new() { Text = "60秒診断記録", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label _health = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label _openTrackStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label _initialization = new()
    {
        Dock = DockStyle.Fill, Padding = new(10, 6, 10, 6), TextAlign = ContentAlignment.MiddleLeft,
        AccessibleDescription = "初期化の案内", BackColor = Color.AliceBlue, ForeColor = Color.MidnightBlue
    };
    private readonly InputPanel _openTrackPose = new("OpenTrack 送信角度", new[] { "Yaw", "Pitch", "Roll" }, Array.Empty<string>());
    private readonly InputPanel _input = new("VADER 拡張入力", new[] { "Gyro X", "Gyro Y", "Gyro Z", "Accel X", "Accel Y", "Accel Z", "Roll", "Pitch", "Yaw", "Yaw速度", "Yaw偏差", "Yaw dt" }, Buttons.Select(b => b.Name));
    private readonly InputPanel _standard = new("VADER 通常入力", new[] { "LX", "LY", "RX", "RY", "LT", "RT" }, VJoyStandardOutput.SourceButtons.Select(b => b.Name));
    private readonly InputPanel _standardVjoy = new("vJoy 1 通常入力・追加ボタン・書込み値", new[] { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider1", "Slider2" }, Enumerable.Range(1, 20).Select(i => i.ToString()));
    private readonly InputPanel _vjoy = new("vJoy 2 拡張入力・書込み値", new[] { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider1", "Slider2" }, Enumerable.Range(1, 10).Select(i => i.ToString()));
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private bool _busy, _closing, _closedCleanly, _exitRequested, _automatic;
    private Task? _operation;
    private long _nextOutputRetry;
    private string _trayPhase = "", _commandError = "", _launchError = "";
    private string _initializationPhase = "";

    internal BridgeDashboardForm(Session session, ResidentSettings settings)
    {
        _session = session; _settings = settings;
        Icon = _images.Application;
        _engine = new(session);
        _engine.SetOpenTrackEnabled(settings.OpenTrackOutputEnabled);
        _openTrack.Checked = _engine.Snapshot().OpenTrack.Enabled;
        Text = $"VADER Bridge {Session.BuildId} — vJoy 1 / OpenTrack"; Size = new(1360, 1080); MinimumSize = new(1120, 800);
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Yu Gothic UI", 10);
        _initialization.Font = new Font(Font.FontFamily, 13, FontStyle.Bold);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new(8) };
        layout.RowStyles.Add(new(SizeType.Absolute, 90)); layout.RowStyles.Add(new(SizeType.Absolute, 36)); layout.RowStyles.Add(new(SizeType.Absolute, 104)); layout.RowStyles.Add(new(SizeType.Absolute, 56)); layout.RowStyles.Add(new(SizeType.Absolute, 32)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 65));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        _method.Items.AddRange(new object[] { "WriteFile", "HidD_SetOutputReport" }); _method.SelectedIndex = 0;
        toolbar.Controls.AddRange(new Control[] { _resume, _pause, _logToggle, _display, _openTrack, _settingsButton, _exit });
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(new Label { Text = $"測定ID: {session.MeasurementId}  版: {Session.BuildId}  出力先: vJoy 1 / OpenTrack  vJoy 2: {(_engine.Snapshot().ExtendedEnabled ? "ON" : "OFF")}", Dock = DockStyle.Fill }, 0, 1);
        layout.Controls.Add(_initialization, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.Controls.Add(_openTrackStatus, 0, 4);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var panels = new TableLayoutPanel { Dock = DockStyle.Top, Height = 1388, ColumnCount = 2, RowCount = 3 };
        panels.ColumnStyles.Add(new(SizeType.Percent, 50)); panels.ColumnStyles.Add(new(SizeType.Percent, 50));
        panels.RowStyles.Add(new(SizeType.Absolute, 558)); panels.RowStyles.Add(new(SizeType.Absolute, 530)); panels.RowStyles.Add(new(SizeType.Absolute, 300));
        panels.Controls.Add(_standard, 0, 0); panels.Controls.Add(_input, 1, 0); panels.Controls.Add(_standardVjoy, 0, 1);
        Control extendedPanel = _engine.Snapshot().ExtendedEnabled ? _vjoy : new Label
        {
            Text = "操作案内\nHOME短押しで正面を設定します。\n画面の×でアイコン常駐へ戻ります。\n終了は画面またはアイコンメニューの「終了」を使います。\nログ採取は必要な時に開始・停止できます。",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
        };
        panels.Controls.Add(extendedPanel, 1, 1);
        panels.Controls.Add(_openTrackPose, 0, 2); panels.SetColumnSpan(_openTrackPose, 2);
        scroll.Controls.Add(panels); layout.Controls.Add(scroll, 0, 5); layout.Controls.Add(_health, 0, 6); Controls.Add(layout);
        _open.Click += async (_, _) => { bool writeFile = _method.SelectedIndex == 0; await Command(() => _engine.OpenAsync(writeFile)); };
        _acquire.Click += (_, _) => _engine.RequestAcquire();
        _output.Click += async (_, _) => await Command(_engine.StartOutputAsync);
        _stop.Click += async (_, _) => await Command(_engine.StopAsync);
        _test.Click += async (_, _) => await Command(_engine.SelfTestAsync);
        _trace.Click += async (_, _) => await Command(async () => { await _engine.SetLoggingEnabledAsync(true); _engine.Diagnostic(); });
        _resume.Click += async (_, _) => await StartAutomaticAsync();
        _pause.Click += async (_, _) => await PauseAsync();
        _logToggle.Click += async (_, _) => await ToggleLoggingAsync();
        _settingsButton.Click += (_, _) => ShowSettings();
        _exit.Click += (_, _) => ExitApplication();
        _display.CheckedChanged += (_, _) => _engine.Log.Write("display", new { enabled = _display.Checked });
        _openTrack.CheckedChanged += (_, _) => _engine.SetOpenTrackEnabled(_openTrack.Checked);
        _timer.Tick += async (_, _) =>
        {
            RefreshDisplay();
            var view = _engine.Snapshot();
            if (_automatic && !_busy && !_closing && view.Running && !view.OutputActive && Environment.TickCount64 >= _nextOutputRetry)
            {
                _nextOutputRetry = Environment.TickCount64 + 2000;
                await Command(_engine.StartOutputAsync);
            }
        };
        FormClosing += OnClosing;
        ConfigureTray();
        RefreshDisplay();
    }
    internal void StartResident()
    {
        _ = Handle;
        _tray.Visible = true;
        _timer.Start();
        BeginInvoke(new Action(async () => await StartAutomaticAsync()));
    }
    private void ConfigureTray()
    {
        _tray.Icon = _images.Starting; _tray.Text = "VADER Bridge / 開始待ち";
        _tray.ContextMenuStrip = _trayMenu;
        _trayMenu.Items.Add(_trayStatus); _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("状態画面を開く", null, (_, _) => ShowDetails());
        _trayMenu.Items.Add(_trayPause);
        _trayPause.Click += async (_, _) => { if (_automatic) await PauseAsync(); else await StartAutomaticAsync(); };
        _trayMenu.Items.Add(_trayLogging);
        _trayLogging.Click += async (_, _) => await ToggleLoggingAsync();
        _trayMenu.Items.Add("設定", null, (_, _) => ShowSettings());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("終了", null, (_, _) => ExitApplication());
        _tray.DoubleClick += (_, _) => ShowDetails();
        _tray.BalloonTipClicked += (_, _) => ShowDetails();
    }
    private async Task StartAutomaticAsync()
    {
        if (_busy || _closing) return;
        _automatic = true; _commandError = ""; _launchError = "";
        try { _settings.StartOpenTrack(); }
        catch (Exception ex) { _launchError = ex.Message; }
        _nextOutputRetry = Environment.TickCount64 + 2000;
        await Command(async () =>
        {
            await _engine.OpenAsync(_settings.UseWriteFile);
            _engine.RequestAcquire();
            await _engine.StartOutputAsync();
        });
    }
    private async Task PauseAsync()
    {
        if (_busy || _closing) return;
        _automatic = false;
        await Command(_engine.StopAsync);
    }
    private Task ToggleLoggingAsync() => Command(() => _engine.SetLoggingEnabledAsync(!_engine.Log.Enabled));
    private void ShowDetails()
    {
        if (_closing) return;
        Show(); WindowState = FormWindowState.Normal; Activate(); RefreshDisplay();
    }
    private void ShowSettings()
    {
        if (_closing || _busy) return;
        ShowDetails();
        using var dialog = new ResidentSettingsForm(_settings, _session.Root);
        dialog.Icon = _images.Application;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _settings = dialog.Settings; _launchError = "";
            _openTrack.Checked = _settings.OpenTrackOutputEnabled;
            _engine.SetOpenTrackEnabled(_settings.OpenTrackOutputEnabled);
        }
    }
    private void ExitApplication() { _exitRequested = true; Close(); }
    protected override void WndProc(ref Message message)
    {
        if ((uint)message.Msg == SingleInstance.ShowMessage) ShowDetails();
        base.WndProc(ref message);
    }
    private async Task Command(Func<Task> action)
    {
        if (_busy || _closing) return; _busy = true; RefreshDisplay();
        try { _operation = Task.Run(action); await _operation; _commandError = ""; }
        catch (Exception ex) { _engine.Log.Write("command-error", new { error = ex.ToString() }); _commandError = ex.Message; }
        finally { _busy = false; if (!_closing) RefreshDisplay(); }
    }
    private void RefreshDisplay()
    {
        var view = _engine.Snapshot();
        _resume.Enabled = !_busy && !_automatic; _pause.Enabled = !_busy && _automatic;
        _logToggle.Enabled = _settingsButton.Enabled = _trayLogging.Enabled = _trayPause.Enabled = !_busy;
        _logToggle.Text = _trayLogging.Text = _engine.Log.Enabled ? "ログ採取停止" : "ログ採取開始";
        _trayLogging.Checked = _engine.Log.Enabled;
        _trayPause.Text = _automatic ? "一時停止" : "再開";
        _open.Enabled = !_busy && !view.Running; _method.Enabled = _open.Enabled;
        _acquire.Enabled = !_busy && view.Running && !view.Acquired; _output.Enabled = !_busy && !view.OutputActive;
        _stop.Enabled = !_busy && (view.Running || view.OutputActive); _test.Enabled = !_busy && !view.Running && !view.OutputActive;
        string status = $"{view.State}  受信:{view.Received}  処理:{view.Processed}  デコード:{view.Decoded}  出力:{view.Written}  処理待ち:{view.Queued}\n{view.Detail}";
        if (_status.Text != status) _status.Text = status;
        string health = _engine.Log.Enabled
            ? $"ログON  ファイル記録:{_engine.Log.Written}件  保存待ち:{_engine.Log.Accepted - _engine.Log.Written}件  容量超過:{_engine.Log.Dropped}件  {_engine.Log.Error}\n{_engine.Log.Path}"
            : $"ログOFF  診断が必要な時は「ログ採取開始」を押してください。\n{(_engine.Log.Path.Length > 0 ? "直前の記録: " + _engine.Log.Path : "")}";
        if (_health.Text != health) _health.Text = health;
        var ot = view.OpenTrack;
        string otStatus = $"OpenTrack {ot.Target}  {(ot.Enabled ? "ON" : "OFF")}  {(ot.InputActive ? ot.BiasReady ? "角度送信" : "静止偏差採集中 / 暫定角度送信" : "入力待機")}  送信:{ot.Sent}  エラー:{ot.Errors}  {ot.Error}";
        if (_openTrackStatus.Text != otStatus) _openTrackStatus.Text = otStatus;
        RefreshInitialization(view);
        RefreshTray(view);
        if (!_display.Checked || !Visible) return;
        _openTrackPose.SetStatus("UDP送信値 / HOME短押しで正面設定");
        SetUnit(_openTrackPose.Axes["Yaw"], ot.Angles.Yaw, 180, "°");
        SetUnit(_openTrackPose.Axes["Pitch"], ot.Angles.Pitch, 89, "°");
        SetUnit(_openTrackPose.Axes["Roll"], ot.Angles.Roll, 89, "°");
        _standard.SetStatus(view.State);
        _input.SetStatus(view.Yaw.BiasReady ? $"{view.State} / Yaw偏差確定" : $"{view.State} / 約2秒静止: {view.Yaw.BiasSamples}件・{view.Yaw.BiasDurationSeconds:F1}秒");
        _standardVjoy.SetStatus(view.StandardOutput.Count > 0 ? "書込み中 / Device 1" : "出力待機");
        _vjoy.SetStatus(!view.ExtendedEnabled ? "Device 2 / 出力OFF" : view.ExtendedActive ? "書込み中 / Device 2" : "出力待機");
        if (view.Input is { } input)
        {
            for (int i = 0; i < 3; i++) { SetNumber(_input.Axes[$"Gyro {"XYZ"[i]}"], input.Gyro[i], 4); SetNumber(_input.Axes[$"Accel {"XYZ"[i]}"], input.Accel[i], 20); }
            SetNumber(_input.Axes["Roll"], view.Roll, 45); SetNumber(_input.Axes["Pitch"], view.Pitch, 45);
            SetUnit(_input.Axes["Yaw"], view.Yaw.AngleDeg, view.Yaw.RangeDeg, "°");
            SetUnit(_input.Axes["Yaw速度"], view.Yaw.CorrectedRateDegPerSecond, 180, "°/s");
            SetUnit(_input.Axes["Yaw偏差"], view.Yaw.BiasDegPerSecond, 3, "°/s");
            SetUnit(_input.Axes["Yaw dt"], view.Yaw.DtSeconds * 1000, 250, "ms");
            foreach (var button in Buttons) _input.Buttons[button.Name].Set(input.Buttons[button.Index]);
            var s = input.Standard;
            foreach (var axis in new[] { ("LX", (int)s.LX), ("LY", (int)s.LY), ("RX", (int)s.RX), ("RY", (int)s.RY) })
                _standard.Axes[axis.Item1].Set(axis.Item2 < 0 ? axis.Item2 / 32768d : axis.Item2 / 32767d, axis.Item2);
            _standard.Axes["LT"].Set(s.LT / 255d * 2 - 1, s.LT); _standard.Axes["RT"].Set(s.RT / 255d * 2 - 1, s.RT);
            _standard.SetPov(s.Pov);
            foreach (var button in VJoyStandardOutput.SourceButtons) _standard.Buttons[button.Name].Set(input.Buttons[button.SourceIndex]);
        }
        for (int button = 1; button <= 10; button++) _vjoy.Buttons[button.ToString()].Set(view.OutputButtons.TryGetValue(button, out bool down) && down);
        foreach (string axis in _vjoy.Axes.Keys)
            if (view.Output.TryGetValue(axis, out int value)) _vjoy.Axes[axis].Set(view.Normalized[axis], value);
            else _vjoy.Axes[axis].Set(0, "待機");
        for (int button = 1; button <= 20; button++) _standardVjoy.Buttons[button.ToString()].Set(view.StandardButtons.TryGetValue(button, out bool down) && down);
        foreach (string axis in _standardVjoy.Axes.Keys)
            if (axis == "Rz") _standardVjoy.Axes[axis].Set(0, "未使用 / 中央");
            else if (view.StandardOutput.TryGetValue(axis, out int value)) _standardVjoy.Axes[axis].Set(view.StandardNormalized[axis], value);
            else _standardVjoy.Axes[axis].Set(0, "待機");
        _standardVjoy.SetPov(view.StandardPov);
    }
    private void RefreshInitialization(BridgeEngine.View view)
    {
        string phase, message;
        Color background = Color.AliceBlue, foreground = Color.MidnightBlue;
        bool delayed = view.AcquireElapsedSeconds >= 5;
        string elapsed = $"開始から {view.AcquireElapsedSeconds:F1}秒 / 目標5秒";
        if (!view.Running)
        {
            phase = "idle";
            message = "一時停止\n「開始／再開」で自動取得と出力を開始します。初回は約2秒、パッドを静止させてください。";
        }
        else if (!view.Acquired)
        {
            phase = "await-acquire";
            message = "自動開始中\n取得要求を準備しています。初回は約2秒の静止測定を行います。";
        }
        else if (view.State is "回復中" or "入力回復中" or "機器設定待ち")
        {
            phase = "recovery-" + view.State;
            background = Color.LemonChiffon; foreground = Color.SaddleBrown;
            string reason = view.State switch
            {
                "機器設定待ち" => "機器設定の通信が落ち着くのを待っています。最終値を保持しています。",
                "入力回復中" => "正常入力を待ちながら、取得を再試行しています。最終値を保持しています。",
                _ => "パッドの接続を開き直しています。最終値を保持しています。"
            };
            message = $"{(view.Yaw.BiasReady ? "復帰中！" : "初期化中！ （パッドを動かさないでください）")}\n{reason}";
            if (!view.Yaw.BiasReady)
            {
                if (delayed) { background = Color.MistyRose; foreground = Color.DarkRed; }
                message += $"\n{elapsed}{(delayed ? " / 接続・通信の回復待ち" : "")}";
            }
        }
        else if (view.Input is null)
        {
            phase = delayed ? "input-delayed" : "input-wait";
            background = delayed ? Color.MistyRose : Color.LemonChiffon;
            foreground = delayed ? Color.DarkRed : Color.SaddleBrown;
            message = $"初期化中！ パッドの入力を待っています\n{elapsed}";
            if (delayed) message += "\n5秒を超えています。機器からの正常入力を待ちながら、取得を再試行しています。";
        }
        else if (!view.Yaw.BiasReady)
        {
            phase = (delayed ? "bias-delayed-" : "bias-") + (view.Yaw.Stationary ? "stationary" : "motion");
            background = delayed ? Color.MistyRose : Color.LemonChiffon;
            foreground = delayed ? Color.DarkRed : Color.SaddleBrown;
            double remaining = Math.Max(0, view.BiasCalibrationSeconds - view.Yaw.BiasDurationSeconds);
            string progress = remaining > 0 ? $"残り約{remaining:F1}秒" : "測定データを収集中";
            string output = view.OpenTrack.Enabled && view.OpenTrack.InputActive ? "暫定角度を送信中" : "入力を受信中";
            message = $"初期化中！ （パッドを動かさないでください）\n静止測定 {view.Yaw.BiasDurationSeconds:F1} / {view.BiasCalibrationSeconds:F1}秒・{progress}｜{elapsed}";
            message += delayed
                ? $"\n5秒を超えています：{(view.Yaw.Stationary ? "静止データの確定を待っています" : "静止を確認しています。パッドを置いてください")}。{output}。"
                : $"\n{output}。静止測定の完了でゼロ点を補正します。";
        }
        else
        {
            phase = "ready";
            background = Color.Honeydew; foreground = Color.DarkGreen;
            message = "姿勢の初期化完了\nHOME短押しで正面を設定できます。";
            if (!view.OutputActive) message += " vJoyの出力準備を続けています。";
        }
        if (_initialization.Text != message) _initialization.Text = message;
        if (_initialization.BackColor != background) _initialization.BackColor = background;
        if (_initialization.ForeColor != foreground) _initialization.ForeColor = foreground;
        if (_initializationPhase != phase)
        {
            _initializationPhase = phase;
            _engine.Log.Write("initialization-display", new { phase, message, acquireElapsedSeconds = view.AcquireElapsedSeconds,
                startupTargetSeconds = 5, stationary = view.Yaw.Stationary, biasDurationSeconds = view.Yaw.BiasDurationSeconds });
        }
    }
    private void RefreshTray(BridgeEngine.View view)
    {
        string phase, title, message;
        Icon icon; ToolTipIcon notice = ToolTipIcon.Info;
        string error = _commandError.Length > 0 ? _commandError
            : _launchError.Length > 0 ? _launchError
            : _engine.Log.Error ?? view.OpenTrack.Error;
        if (error.Length > 0 || view.State is "出力を再試行" or "回復処理の結果を確認")
        {
            phase = "error-" + error; title = "状態を確認してください";
            message = error.Length > 0 ? error : view.Detail; icon = _images.Error; notice = ToolTipIcon.Error;
        }
        else if (!_automatic && !view.Running)
        {
            phase = "paused"; title = "一時停止"; message = "アイコンメニューの「再開」で取得と出力を開始できます。"; icon = _images.Waiting;
        }
        else if (view.State is "回復中" or "入力回復中" or "機器設定待ち")
        {
            phase = "recovery"; title = "再接続中";
            message = "接続と入力の回復を待っています。最後の出力値を保持しています。"; icon = _images.Waiting;
        }
        else if (!view.Running || view.Input is null || !view.Yaw.BiasReady)
        {
            phase = "initializing"; title = "初期化中！";
            message = "（パッドを動かさないでください）\n約2秒の静止測定と接続準備を行っています。"; icon = _images.Starting;
        }
        else if (!view.OutputActive)
        {
            phase = "output-wait"; title = "vJoyの出力準備中";
            message = "vJoyの設定と使用状態を確認しながら、出力開始を再試行しています。"; icon = _images.Waiting;
        }
        else
        {
            phase = "ready"; title = "使用可能";
            message = "vJoyへ出力しています。HOME短押しでOpenTrackの正面を設定できます。"; icon = _images.Ready;
        }
        _trayStatus.Text = title;
        string tooltip = $"VADER Bridge {Session.BuildId} / {title} / ログ{(_engine.Log.Enabled ? "ON" : "OFF")}";
        if (_tray.Text != tooltip) _tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        if (_tray.Icon != icon) _tray.Icon = icon;
        if (_trayPhase != phase)
        {
            _trayPhase = phase;
            if (_tray.Visible && !_closing) _tray.ShowBalloonTip(5000, "VADER Bridge — " + title, message, notice);
        }
    }
    private static void SetNumber(AxisMeter meter, double value, double scale) => meter.Set(value / scale, value.ToString("+0000.0000;-0000.0000;+0000.0000", CultureInfo.InvariantCulture));
    private static void SetUnit(AxisMeter meter, double value, double scale, string unit) => meter.Set(value / scale, value.ToString("+0000.0000;-0000.0000;+0000.0000", CultureInfo.InvariantCulture) + " " + unit);
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closedCleanly) return;
        if (e.CloseReason == CloseReason.WindowsShutDown)
        {
            _closing = true; _automatic = false; _timer.Stop();
            try
            {
                if (_operation is not null) { try { _operation.GetAwaiter().GetResult(); } catch { } }
                _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) { _engine.Log.Write("shutdown-error", new { error = ex.ToString() }); }
            finally { DisposeResidentUI(); _closedCleanly = true; }
            return;
        }
        e.Cancel = true;
        if (_closing) return;
        if (!_exitRequested && e.CloseReason is not CloseReason.WindowsShutDown and not CloseReason.TaskManagerClosing)
        {
            Hide(); return;
        }
        _closing = true; _automatic = false; _timer.Stop(); Enabled = false;
        try
        {
            if (_operation is not null) { try { await _operation; } catch { } }
            await _engine.DisposeAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bridge 終了結果"); }
        DisposeResidentUI(); _closedCleanly = true; Close();
    }
    private void DisposeResidentUI()
    {
        _tray.Visible = false; _tray.Dispose(); _trayMenu.Dispose(); _images.Dispose(); _timer.Dispose();
    }
}
