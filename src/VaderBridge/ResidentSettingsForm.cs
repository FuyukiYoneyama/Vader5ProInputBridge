internal sealed class ResidentSettingsForm : Form
{
    internal ResidentSettings Settings { get; private set; }
    internal ResidentSettingsForm(ResidentSettings current, string root)
    {
        Settings = current;
        Text = "VADER Bridge 設定"; AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 10); ClientSize = new(660, 365);
        MinimumSize = Size; FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
        var login = new CheckBox { Text = "Windowsログオン時にBridgeを起動", Checked = current.StartOnLogin, AutoSize = true };
        var launch = new CheckBox { Text = "Bridge起動時にOpenTrackを起動", Checked = current.LaunchOpenTrack, AutoSize = true };
        var output = new CheckBox { Text = "OpenTrackへ姿勢角度を送信", Checked = current.OpenTrackOutputEnabled, AutoSize = true };
        var path = new TextBox { Text = current.OpenTrackPath, Dock = DockStyle.Fill };
        var browse = new Button { Text = "参照", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "OpenTrackの実行ファイル", Filter = "実行ファイル (*.exe)|*.exe", FileName = path.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.FileName;
        };
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        pathRow.ColumnStyles.Add(new(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new(SizeType.Absolute, 82));
        pathRow.Controls.Add(path, 0, 0); pathRow.Controls.Add(browse, 1, 0);
        var method = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 300 };
        method.Items.AddRange(new object[] { "WriteFile（診断用の送信方式）", "HidD_SetOutputReport（診断用の送信方式）" });
        method.SelectedIndex = current.UseWriteFile ? 0 : 1;
        var save = new Button { Text = "保存", AutoSize = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { cancel, save });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 8 };
        foreach (int height in new[] { 36, 36, 32, 40, 36, 44, 48, 50 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.Controls.Add(login, 0, 0); layout.Controls.Add(launch, 0, 1);
        layout.Controls.Add(new Label { Text = "OpenTrackの実行ファイル", AutoSize = true }, 0, 2);
        layout.Controls.Add(pathRow, 0, 3); layout.Controls.Add(output, 0, 4);
        layout.Controls.Add(method, 0, 5);
        layout.Controls.Add(new Label { Text = "起動時のログはOFFです。アイコンメニューから採取できます。\n送信方式の変更は、一時停止後の再開から適用します。", Dock = DockStyle.Fill }, 0, 6);
        layout.Controls.Add(buttons, 0, 7); Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        save.Click += (_, _) =>
        {
            try
            {
                var candidate = new ResidentSettings { StartOnLogin = login.Checked, LaunchOpenTrack = launch.Checked,
                    OpenTrackPath = path.Text.Trim(), OpenTrackOutputEnabled = output.Checked, UseWriteFile = method.SelectedIndex == 0 };
                candidate.Save(root); Settings = candidate; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定の保存結果", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
    }
}
