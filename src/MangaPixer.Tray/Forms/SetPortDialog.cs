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
        ClientSize = new Size(360, 130);
        ShowIcon = false;
        ShowInTaskbar = false;

        var promptLabel = new Label
        {
            Text = "MangaPixer will use this port the next time the server restarts:",
            AutoSize = false,
            Location = new Point(12, 12),
            Size = new Size(336, 32),
        };

        _portTextBox = new TextBox
        {
            Text = currentPort.ToString(),
            Location = new Point(12, 48),
            Size = new Size(100, 23),
        };

        _errorLabel = new Label
        {
            ForeColor = Color.Firebrick,
            AutoSize = false,
            Location = new Point(12, 74),
            Size = new Size(336, 32),
            Visible = false,
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.None,
            Location = new Point(192, 95),
            Size = new Size(75, 23),
        };
        okButton.Click += OnOkClicked;

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(273, 95),
            Size = new Size(75, 23),
        };

        Controls.AddRange([promptLabel, _portTextBox, _errorLabel, okButton, cancelButton]);
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
