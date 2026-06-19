namespace JBTheatreTools;

public sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _token = new();
    private readonly Label _tokenState = new();
    private readonly Button _save = new();
    private readonly Button _remove = new();
    private readonly ComboBox _appearance = new();

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 280);

        var heading = new Label
        {
            Text = "GitHub access token",
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 16),
        };

        _tokenState.AutoSize = true;
        _tokenState.Location = new Point(16, 42);
        UpdateTokenState();

        _token.UseSystemPasswordChar = true;
        _token.PlaceholderText = "Paste a fine-grained PAT…";
        _token.Location = new Point(16, 66);
        _token.Width = 428;

        _save.Text = "Save";
        _save.Location = new Point(16, 98);
        _save.Click += (_, _) =>
        {
            var t = _token.Text.Trim();
            if (t.Length == 0) return;
            TokenStore.Save(t);
            _token.Clear();
            UpdateTokenState();
        };

        _remove.Text = "Remove";
        _remove.Location = new Point(_save.Right + 8, 98);
        _remove.Click += (_, _) =>
        {
            TokenStore.Clear();
            UpdateTokenState();
        };

        var tokenLink = new LinkLabel
        {
            Text = "Create a fine-grained token on GitHub →",
            AutoSize = true,
            Location = new Point(16, 132),
        };
        tokenLink.LinkClicked += (_, _) => OpenUrl("https://github.com/settings/personal-access-tokens/new");

        var help = new Label
        {
            Text = "Give it Contents: Read-only on your tool repos.",
            AutoSize = false,
            Location = new Point(16, 154),
            Size = new Size(428, 20),
            ForeColor = Color.Gray,
        };

        var appearanceHeading = new Label
        {
            Text = "Appearance",
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 178),
        };

        _appearance.DropDownStyle = ComboBoxStyle.DropDownList;
        _appearance.Items.AddRange(new object[] { "System", "Light", "Dark" });
        _appearance.SelectedIndex = _settings.Appearance switch { "light" => 1, "dark" => 2, _ => 0 };
        _appearance.Location = new Point(16, 202);
        _appearance.Width = 160;
        _appearance.SelectedIndexChanged += (_, _) =>
            _settings.Appearance = _appearance.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };

        var done = new Button
        {
            Text = "Done",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 100, 240),
            Width = 84,
        };
        AcceptButton = done;

        Controls.AddRange(new Control[]
        {
            heading, _tokenState, _token, _save, _remove, tokenLink, help, appearanceHeading, _appearance, done
        });
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { /* non-fatal */ }
    }

    private void UpdateTokenState()
    {
        bool has = TokenStore.Load() != null;
        _tokenState.Text = has
            ? "A token is saved in Credential Manager."
            : "No token saved — downloads are disabled until you add one.";
        _tokenState.ForeColor = has ? Color.SeaGreen : Color.Gray;
        _remove.Visible = has;
    }

    public void ApplyTheme(bool dark)
    {
        BackColor = Theme.Bg(dark);
        ForeColor = Theme.Fg(dark);
        if (IsHandleCreated) Theme.ApplyTitleBar(this, dark);
    }
}
