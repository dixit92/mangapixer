using com.lifepixer.mangapixer.Tray.Server;

namespace com.lifepixer.mangapixer.Tray.Forms;

/// <summary>
/// "Set Port..." dialog: a textbox pre-filled with the current port plus an
/// inline error label. All validation lives in <see cref="PortInputValidator"/>
/// — this class only wires the OK button to it and keeps the dialog open (with
/// the error shown) on failure, matching the LAN-toggle dialog's tone rather
/// than popping a separate error MessageBox.
/// </summary>
public sealed class SetPortDialog : Form
{
    private readonly PortInputValidator _validator;
    private readonly TextBox _portTextBox;
    private readonly Label _errorLabel;
    private readonly int _currentPort;

    public int SelectedPort { get; private set; }

    public SetPortDialog(int currentPort, PortInputValidator? validator = null)
    {
        _validator = validator ?? new PortInputValidator();
        _currentPort = currentPort;

        Text = "Set Port";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowIcon = false;
        ShowInTaskbar = false;

        // Every control below is AutoSize and the form/panels grow to fit them,
        // so the dialog scales correctly under the app's SystemAware high-DPI
        // mode instead of clipping (the old fixed-pixel/96-DPI layout did not).
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var promptLabel = new Label
        {
            Text = "MangaPixer will use this port the next time the server restarts:",
            AutoSize = true,
            MaximumSize = new Size(336, 0),
            Margin = new Padding(0, 0, 0, 12),
        };

        _portTextBox = new TextBox
        {
            Text = currentPort.ToString(),
            Width = 100,
            Margin = new Padding(0, 0, 0, 12),
        };

        _errorLabel = new Label
        {
            ForeColor = Color.Firebrick,
            AutoSize = true,
            MaximumSize = new Size(336, 0),
            Margin = new Padding(0, 0, 0, 12),
            Visible = false,
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.None,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0),
        };
        okButton.Click += OnOkClicked;

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(0),
        };

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        buttonRow.Controls.Add(okButton);
        buttonRow.Controls.Add(cancelButton);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(promptLabel);
        layout.Controls.Add(_portTextBox);
        layout.Controls.Add(_errorLabel);
        layout.Controls.Add(buttonRow);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void OnOkClicked(object? sender, EventArgs e)
    {
        var result = _validator.Validate(_portTextBox.Text, _currentPort);
        if (!result.IsValid)
        {
            _errorLabel.Text = result.ErrorMessage;
            _errorLabel.Visible = true;
            return;
        }

        SelectedPort = result.Port;
        DialogResult = DialogResult.OK;
        Close();
    }
}
