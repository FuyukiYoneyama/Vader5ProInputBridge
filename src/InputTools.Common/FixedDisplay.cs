using System.Globalization;

namespace InputTools;

public sealed class InputLamp : Label
{
    private bool? _previous;
    public InputLamp(string name)
    {
        Text = name; AutoSize = false; Size = new(64, 30); TextAlign = ContentAlignment.MiddleCenter;
        Margin = new(3); BackColor = Color.DimGray; ForeColor = Color.White;
    }
    public void Set(bool active)
    {
        if (_previous == active) return;
        _previous = active; BackColor = active ? Color.ForestGreen : Color.DimGray;
    }
}

public sealed class AxisMeter : TableLayoutPanel
{
    private readonly Label _value = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Consolas", 11) };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Margin = new(8, 7, 8, 7) };
    private string? _previous;
    public AxisMeter(string name)
    {
        Height = 32; Dock = DockStyle.Fill; ColumnCount = 3; RowCount = 1;
        ColumnStyles.Add(new(SizeType.Absolute, 90)); ColumnStyles.Add(new(SizeType.Percent, 100)); ColumnStyles.Add(new(SizeType.Absolute, 210));
        Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        Controls.Add(_bar, 1, 0); Controls.Add(_value, 2, 0);
    }
    public void Set(double normalized, string value)
    {
        if (_previous == value) return;
        _previous = value; _value.Text = value;
        _bar.Value = Math.Clamp((int)Math.Round((Math.Clamp(normalized, -1, 1) + 1) * 500), 0, 1000);
    }
    public void Set(double normalized, int raw) => Set(normalized, $"{raw,7}  {normalized.ToString("+0.0000;-0.0000;+0.0000", CultureInfo.InvariantCulture),8}");
}

public sealed class InputPanel : GroupBox
{
    private readonly Label _status = new() { AutoSize = true };
    private readonly Label _pov = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Consolas", 11) };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Fill, WrapContents = true };
    private readonly TableLayoutPanel _axes = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 0 };
    public Dictionary<string, AxisMeter> Axes { get; } = new();
    public Dictionary<string, InputLamp> Buttons { get; } = new();
    public InputPanel(string title, IEnumerable<string> axes, IEnumerable<string> buttons)
    {
        Text = title; Dock = DockStyle.Fill; Padding = new(10);
        string[] buttonNames = buttons.ToArray();
        int buttonHeight = Math.Max(80, ((buttonNames.Length + 4) / 5) * 36 + 8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 28)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 28)); layout.RowStyles.Add(new(SizeType.Absolute, buttonHeight));
        foreach (string name in axes) AddAxis(name);
        foreach (string name in buttonNames) { var lamp = new InputLamp(name); Buttons.Add(name, lamp); _buttons.Controls.Add(lamp); }
        MinimumSize = new(430, Math.Max(300, Axes.Count * 32 + 94 + buttonHeight));
        layout.Controls.Add(_status, 0, 0); layout.Controls.Add(_axes, 0, 1); layout.Controls.Add(_pov, 0, 2); layout.Controls.Add(_buttons, 0, 3); Controls.Add(layout);
    }
    public void AddAxis(string name)
    {
        var meter = new AxisMeter(name); Axes.Add(name, meter); _axes.RowStyles.Add(new(SizeType.Percent, 1));
        _axes.Controls.Add(meter, 0, _axes.RowCount++);
    }
    public void SetStatus(string text) { if (_status.Text != text) _status.Text = text; }
    public void SetPov(uint value)
    {
        string direction = value switch { uint.MaxValue => "中央", 0 => "↑", 4500 => "↗", 9000 => "→", 13500 => "↘", 18000 => "↓", 22500 => "↙", 27000 => "←", 31500 => "↖", _ => "角度" };
        string text = value == uint.MaxValue ? $"十字キー: {direction}" : $"十字キー: {direction}  生値 {value,5}  {value / 100d,6:0.00}度";
        if (_pov.Text != text) _pov.Text = text;
    }
}
