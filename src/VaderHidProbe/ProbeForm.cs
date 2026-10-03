using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class ProbeForm : Form
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    private readonly ComboBox _interfaces = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
    private readonly CheckBox _diffOnly = new() { Text = "差分のみ", Checked = true, AutoSize = true };
    private readonly Button _start = new() { Text = "入力キャプチャ開始", AutoSize = true };
    private readonly Button _stop = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly TextBox _output = new()
    {
        Multiline = true,
        ReadOnly = true,
        Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 10)
    };
    private CancellationTokenSource? _captureCancellation;

    public ProbeForm(string enumeration, IReadOnlyList<Program.CaptureTarget> targets)
    {
        Text = "Vader 5 Pro HID Probe - Phase 1 / 2";
        Width = 1150;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        foreach (Program.CaptureTarget target in targets)
            _interfaces.Items.Add(target);
        if (_interfaces.Items.Count > 0)
            _interfaces.SelectedIndex = 0;

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(6),
            WrapContents = false
        };
        toolbar.Controls.Add(new Label { Text = "HID interface:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        toolbar.Controls.Add(_interfaces);
        toolbar.Controls.Add(_diffOnly);
        toolbar.Controls.Add(_start);
        toolbar.Controls.Add(_stop);
        Controls.Add(_output);
        Controls.Add(toolbar);

        _output.Text = enumeration;
        _start.Click += (_, _) => StartCapture();
        _stop.Click += (_, _) => _captureCancellation?.Cancel();
        FormClosing += (_, _) => _captureCancellation?.Cancel();
    }

    private void StartCapture()
    {
        if (_interfaces.SelectedItem is not Program.CaptureTarget target)
            return;
        _captureCancellation = new CancellationTokenSource();
        _start.Enabled = false;
        _stop.Enabled = true;
        AppendLine($"\nCapture started: {target.Name}, input length {target.InputReportLength}. Read-only.");
        _ = CaptureAsync(target, _diffOnly.Checked, _captureCancellation.Token);
    }

    private async Task CaptureAsync(Program.CaptureTarget target, bool diffOnly, CancellationToken cancellationToken)
    {
        try
        {
            using SafeFileHandle handle = CreateFile(target.Path, GenericRead, ShareRead | ShareWrite,
                IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                AppendLine($"Open failed (Win32 {Marshal.GetLastWin32Error()}).");
                return;
            }

            await using var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: true);
            byte[] buffer = new byte[target.InputReportLength];
            byte[]? previous = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) continue;
                byte[] current = buffer.AsSpan(0, count).ToArray();
                string? line = FormatReport(previous, current, diffOnly);
                if (line is not null)
                    AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
                previous = current;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLine($"Capture error: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(() =>
                {
                    _start.Enabled = true;
                    _stop.Enabled = false;
                    AppendLine("Capture stopped.");
                }));
            }
        }
    }

    private static string? FormatReport(byte[]? previous, byte[] current, bool diffOnly)
    {
        if (previous is null)
            return $"baseline ({current.Length} bytes): {Convert.ToHexString(current)}";
        if (!diffOnly)
            return $"raw ({current.Length} bytes): {Convert.ToHexString(current)}";

        var changes = new StringBuilder();
        int maxLength = Math.Max(previous.Length, current.Length);
        for (int offset = 0; offset < maxLength; offset++)
        {
            byte before = offset < previous.Length ? previous[offset] : (byte)0;
            byte after = offset < current.Length ? current[offset] : (byte)0;
            if (offset < previous.Length && offset < current.Length && before == after)
                continue;
            if (changes.Length > 0) changes.Append("; ");
            changes.Append($"offset {offset}: {(offset < previous.Length ? before.ToString("X2") : "--")} -> {(offset < current.Length ? after.ToString("X2") : "--")}");
        }
        return changes.Length == 0 ? null : $"first byte 0x{current[0]:X2}: {changes}";
    }

    private void AppendLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(() =>
        {
            _output.AppendText(line + Environment.NewLine);
            if (_output.Lines.Length > 600)
                _output.Lines = _output.Lines[^500..];
        }));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
