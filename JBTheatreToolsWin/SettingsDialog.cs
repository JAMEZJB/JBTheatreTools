namespace JBTheatreTools;

public sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly SelfInfo? _selfInfo;
    private readonly string _currentVersion;

    private readonly TextBox _token = new();
    private readonly Label _tokenState = new();
    private readonly Button _save = new();
    private readonly Button _remove = new();
    private readonly ComboBox _updateMode = new();
    private readonly Label _updateHint = new();
    private readonly Button _check = new();
    private readonly Button _viewRelease = new();
    private readonly Label _checkResult = new();
    private readonly ComboBox _appearance = new();
    private readonly ComboBox _closeBehavior = new();
    private readonly CheckBox _installToApps = new();

    public SettingsDialog(AppSettings settings, SelfInfo? selfInfo, string currentVersion)
    {
        _settings = settings;
        _selfInfo = selfInfo;
        _currentVersion = currentVersion;

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 528);

        // --- GitHub token ---
        var tokenHeading = Bold("GitHub access token", new Point(16, 16));

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
            if (MessageBox.Show(this,
                    "Remove the saved token? You'll need to paste one again before you can install or update apps.",
                    "Remove token", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
            Text = "Contents: Read-only. Only repos it can access appear here.",
            AutoSize = false,
            Location = new Point(16, 154),
            Size = new Size(428, 20),
            ForeColor = Color.Gray,
        };

        // --- Updates ---
        var updatesHeading = Bold("Updates", new Point(16, 188));

        _updateMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _updateMode.Items.AddRange(new object[] { "Every launch", "Manual only", "Never" });
        _updateMode.SelectedIndex = _settings.UpdateMode switch { "manual" => 1, "never" => 2, _ => 0 };
        _updateMode.Location = new Point(16, 212);
        _updateMode.Width = 200;
        _updateMode.SelectedIndexChanged += (_, _) =>
        {
            _settings.UpdateMode = _updateMode.SelectedIndex switch { 1 => "manual", 2 => "never", _ => "everyLaunch" };
            _updateHint.Text = UpdateHint();
        };

        _updateHint.AutoSize = false;
        _updateHint.Location = new Point(16, 240);
        _updateHint.Size = new Size(428, 18);
        _updateHint.ForeColor = Color.Gray;
        _updateHint.Text = UpdateHint();

        var versionLabel = new Label
        {
            Text = $"JB Theatre Tools v{_currentVersion}",
            AutoSize = true,
            Location = new Point(16, 268),
        };

        _check.Text = "Check for Updates";
        _check.AutoSize = true;
        _check.Location = new Point(280, 264);
        _check.Click += async (_, _) => await CheckLauncherAsync();

        _viewRelease.Text = "Download Update";
        _viewRelease.AutoSize = true;
        _viewRelease.Visible = false;
        _viewRelease.Click += async (_, _) =>
        {
            if (_selfInfo == null) return;
            _viewRelease.Enabled = false;
            SetResult("Downloading…", Color.Gray);
            try
            {
                var dest = await LauncherUpdate.DownloadAndRevealAsync(_selfInfo);
                SetResult($"Saved {Path.GetFileName(dest)} to Downloads — quit & replace.", Color.SeaGreen);
            }
            catch (Exception ex)
            {
                SetResult(ex.Message, Color.Firebrick);
            }
            finally
            {
                _viewRelease.Enabled = true;
            }
        };

        _checkResult.AutoSize = false;
        _checkResult.Location = new Point(16, 296);
        _checkResult.Size = new Size(428, 20);
        _checkResult.ForeColor = Color.Gray;

        // --- Appearance ---
        var appearanceHeading = Bold("Appearance", new Point(16, 332));

        _appearance.DropDownStyle = ComboBoxStyle.DropDownList;
        _appearance.Items.AddRange(new object[] { "System", "Light", "Dark" });
        _appearance.SelectedIndex = _settings.Appearance switch { "light" => 1, "dark" => 2, _ => 0 };
        _appearance.Location = new Point(16, 356);
        _appearance.Width = 160;
        _appearance.SelectedIndexChanged += (_, _) =>
            _settings.Appearance = _appearance.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };

        // --- Close behaviour ---
        var closeHeading = Bold("When I close the window", new Point(16, 392));

        _closeBehavior.DropDownStyle = ComboBoxStyle.DropDownList;
        _closeBehavior.Items.AddRange(new object[] { "Quit the app", "Keep running in the tray" });
        _closeBehavior.SelectedIndex = _settings.CloseBehavior == "keepRunning" ? 1 : 0;
        _closeBehavior.Location = new Point(16, 416);
        _closeBehavior.Width = 240;
        _closeBehavior.SelectedIndexChanged += (_, _) =>
            _settings.CloseBehavior = _closeBehavior.SelectedIndex == 1 ? "keepRunning" : "quit";

        // --- Install location ---
        // UseMnemonic=false so the literal "&" renders (otherwise "& D" is eaten as an Alt-shortcut).
        _installToApps.UseMnemonic = false;
        _installToApps.Text = "Install apps to the Start menu & Desktop (launch them without this launcher)";
        _installToApps.AutoSize = false;
        _installToApps.Location = new Point(16, 452);
        _installToApps.Size = new Size(428, 34);
        _installToApps.Checked = _settings.InstallToApplications;
        _installToApps.CheckedChanged += (_, _) => _settings.InstallToApplications = _installToApps.Checked;

        var openLog = new Button
        {
            Text = "Open Log",
            Location = new Point(16, 492),
            AutoSize = true,
        };
        openLog.Click += (_, _) => Log.Open();

        var done = new Button
        {
            Text = "Done",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 100, 492),
            Width = 84,
        };
        AcceptButton = done;

        Controls.AddRange(new Control[]
        {
            tokenHeading, _tokenState, _token, _save, _remove, tokenLink, help,
            updatesHeading, _updateMode, _updateHint, versionLabel, _check, _viewRelease, _checkResult,
            appearanceHeading, _appearance, closeHeading, _closeBehavior, _installToApps, openLog, done
        });
    }

    private async Task CheckLauncherAsync()
    {
        if (_selfInfo == null) { SetResult("No self-update info in catalog.", Color.Gray); return; }
        _check.Enabled = false;
        SetResult("Checking…", Color.Gray);
        _viewRelease.Visible = false;
        try
        {
            using var client = new GitHubClient(TokenStore.Load());   // public repo; token optional
            var info = await client.LatestReleaseAsync(_selfInfo.Owner, _selfInfo.Repo);
            if (Versions.IsNewer(info.TagName, _currentVersion))
            {
                SetResult($"{info.TagName} is available.", Color.RoyalBlue);
                _viewRelease.Location = new Point(_checkResult.Left + 160, 292);
                _viewRelease.Visible = true;
            }
            else
            {
                SetResult($"You're up to date (v{_currentVersion}).", Color.SeaGreen);
            }
        }
        catch (GitHubException ge) when (ge.Kind == GitHubErrorKind.NoRelease)
        {
            SetResult("No launcher release published yet.", Color.Gray);
        }
        catch (Exception ex)
        {
            SetResult(ex.Message, Color.Gray);
        }
        finally
        {
            _check.Enabled = true;
        }
    }

    private void SetResult(string text, Color color)
    {
        _checkResult.Text = text;
        _checkResult.ForeColor = color;
    }

    private string UpdateHint() => _settings.UpdateMode switch
    {
        "manual" => "Only checks when you press Refresh or Check for Updates.",
        "never" => "Never checks automatically. You can still install/update from the buttons.",
        _ => "Checks all apps and the launcher each time it opens.",
    };

    private static Label Bold(string text, Point location) => new()
    {
        Text = text,
        Font = new Font(Control.DefaultFont.FontFamily, 10f, FontStyle.Bold),
        AutoSize = true,
        Location = location,
    };

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
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
