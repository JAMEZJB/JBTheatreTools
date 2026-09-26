using System.Globalization;

namespace JBTheatreTools;

/// <summary>Shared chrome for the launcher's secondary windows (release notes, activity, details, import): the
/// house tokens for the current theme, a dark title bar, and a bottom button row. Every dialog here lays out with
/// docking (no hand-placed pixels), so the framework's DPI AutoScale pass sizes it on any monitor.</summary>
internal static class DialogKit
{
    /// <remarks>Leaves the form's layout SUSPENDED: each dialog adds its controls and then calls
    /// <c>ResumeLayout(false)</c>, so the DPI AutoScale pass runs once, on the first layout, over the finished form
    /// (sizes, minimum size and paddings included) — the same order as SettingsDialog.</remarks>
    public static void Style(Form f, bool dark, Size logicalClientSize, bool sizable)
    {
        f.SuspendLayout();
        f.AutoScaleDimensions = new SizeF(96f, 96f);
        f.AutoScaleMode = AutoScaleMode.Dpi;
        f.ClientSize = logicalClientSize;
        f.StartPosition = FormStartPosition.CenterParent;
        f.MinimizeBox = false;
        f.MaximizeBox = sizable;
        f.ShowInTaskbar = false;
        f.FormBorderStyle = sizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
        if (sizable) f.MinimumSize = new Size(logicalClientSize.Width / 2, logicalClientSize.Height / 2);
        f.BackColor = Theme.Bg(dark);
        f.ForeColor = Theme.Fg(dark);
        var font = Theme.Ui(Theme.PtBody);
        f.Font = font;
        f.HandleCreated += (_, _) => Theme.ApplyTitleBar(f, dark);
        f.FormClosed += (_, _) => font.Dispose();
    }

    /// <summary>The one confirmation behind every way of turning show lock OFF (the banner, the More menu, Ctrl+L, the
    /// Settings checkbox). Keep On is the default (Enter) and what Esc or the close box answer: turning the lock off
    /// mid-show takes a deliberate click. True = turn it off.</summary>
    public static bool ConfirmTurnOffShowLock(IWin32Window owner)
    {
        var keep = new TaskDialogButton("Keep On");
        var off = new TaskDialogButton("Turn Off");
        var page = new TaskDialogPage
        {
            Caption = "Turn Off Show Lock?",
            Text = "Installs, updates and uninstalls can run again, including automatic updates if they're switched on.",
            Buttons = { keep, off },
            DefaultButton = keep,
            AllowCancel = true,   // Esc / the close box → TaskDialogButton.Cancel, i.e. not "Turn Off"
        };
        return TaskDialog.ShowDialog(owner, page) == off;
    }

    /// <summary>"Reinstall … as the x64 / ARM64 build?" (the row's ⋯ → "Use the x64 build (emulated)"). Cancel is the
    /// default (Enter) and what Esc or the close box answer. True = reinstall.</summary>
    public static bool ConfirmReinstall(IWin32Window owner, string caption, string text)
    {
        var reinstall = new TaskDialogButton("Reinstall");
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = caption,
            Text = text,
            Buttons = { reinstall, cancel },
            DefaultButton = cancel,
            AllowCancel = true,
        };
        return TaskDialog.ShowDialog(owner, page) == reinstall;
    }

    /// <summary>A pop-up menu built for one showing is disposed once it has closed (after its click has run).</summary>
    public static void DisposeWhenClosed(ContextMenuStrip menu, Control owner)
        => menu.Closed += (_, _) => { if (owner.IsHandleCreated) owner.BeginInvoke(new Action(menu.Dispose)); else menu.Dispose(); };

    /// <summary>A right-aligned button row docked to the bottom; returns it so callers can add buttons (added in
    /// right-to-left order).</summary>
    public static FlowLayoutPanel ButtonRow()
        => new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 10),
            WrapContents = false,
        };

    /// <summary>A house button for a dialog's button row: <paramref name="primary"/> = the accent-filled default action
    /// (the dialog's AcceptButton), otherwise the quiet secondary.</summary>
    public static HouseButton Button(string text, DialogResult result = DialogResult.None, bool primary = false)
        => new(primary ? HouseRole.Primary : HouseRole.Secondary)
        {
            Text = text, AutoSize = true, DialogResult = result, Margin = new Padding(6, 0, 0, 0),
        };

    /// <summary>A read-only rich text view in the panel surface colour, for notes and history.</summary>
    public static RichTextBox Reader(bool dark)
    {
        var reader = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface(dark),
            ForeColor = Theme.Fg(dark),
            DetectUrls = false,
            ShortcutsEnabled = true,   // Ctrl+C / Ctrl+A still work
            WordWrap = true,
        };
        reader.HandleCreated += (_, _) => HouseDraw.NativeTheme(reader, dark);   // a dark scrollbar in dark mode
        return reader;
    }

    /// <summary>Wraps a control in a padded surface panel (the reader's margin).</summary>
    public static Panel Padded(Control inner, bool dark)
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 12), BackColor = Theme.Surface(dark) };
        p.Controls.Add(inner);
        return p;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Pauses (false) or resumes (true) painting of a control; resuming repaints it once.</summary>
    public static void SetRedraw(Control c, bool on)
    {
        if (!c.IsHandleCreated) return;
        SendMessage(c.Handle, 0x000B /* WM_SETREDRAW */, on ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        if (on) c.Invalidate(true);
    }

    public static void Append(RichTextBox box, string text, Font font, Color color)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionFont = font;
        box.SelectionColor = color;
        box.SelectedText = text;
    }
}

/// <summary>In-app release notes for one app: every release, newest version first, with its date and notes
/// (Markdown rendered as plain text by <see cref="ReleaseNotesText"/>). Releases newer than the installed version
/// are flagged "New since your version".</summary>
internal sealed class ReleaseNotesDialog : Form
{
    private readonly List<Font> _fonts = new();

    /// <param name="fallback">Shown when there are no releases (e.g. the launcher's catalog what's-new line).</param>
    public ReleaseNotesDialog(string title, IEnumerable<ReleaseInfo> releases, string? installed, bool dark, string? fallback = null)
    {
        Text = title;
        DialogKit.Style(this, dark, new Size(600, 540), sizable: true);
        var reader = DialogKit.Reader(dark);
        var buttons = DialogKit.ButtonRow();
        var close = DialogKit.Button("Close", DialogResult.Cancel);
        buttons.Controls.Add(close);
        CancelButton = close;
        Controls.Add(DialogKit.Padded(reader, dark));
        Controls.Add(buttons);
        ResumeLayout(false);

        var list = releases.ToList();
        list.Sort((a, b) => VersionCompare.Compare(b.TagName, a.TagName));
        Load += (_, _) =>
        {
            Font F(float pt, bool semi = false) { var f = Theme.Ui(pt, semi, DeviceDpi); _fonts.Add(f); return f; }
            Font head = F(Theme.PtTitle, true), meta = F(Theme.PtSmall), body = F(Theme.PtBody);
            // One repaint for the whole fill: each Append is a selection change, and a long release list
            // otherwise redraws the box dozens of times on the UI thread.
            DialogKit.SetRedraw(reader, false);
            try
            {
                if (list.Count == 0)
                {
                    DialogKit.Append(reader, fallback ?? ReleaseNotesText.Empty, body, Theme.Fg(dark));
                }
                var now = DateTimeOffset.UtcNow;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (i > 0) DialogKit.Append(reader, "\n\n", body, Theme.Fg(dark));
                    DialogKit.Append(reader, VersionCompare.Display(r.TagName), head, Theme.Fg(dark));
                    var bits = new List<string>();
                    if (r.Published is { } p)
                        bits.Add($"{p.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)} ({RelativeAge.Describe(p, now)})");
                    if (r.Prerelease) bits.Add(VersionCompare.IsDev(r.TagName) ? "development build" : "pre-release");
                    if (bits.Count > 0) DialogKit.Append(reader, "   " + string.Join(" · ", bits), meta, Theme.Sub(dark));
                    if (installed != null && VersionCompare.Equal(r.TagName, installed))
                        DialogKit.Append(reader, "   ✓ installed", meta, Theme.Ok);
                    else if (installed != null && VersionCompare.IsNewer(r.TagName, installed))
                        DialogKit.Append(reader, "   New since your version", meta, Theme.Accent);
                    DialogKit.Append(reader, "\n" + ReleaseNotesText.Plain(r.Body), body, Theme.Fg(dark));
                }
                reader.SelectionStart = 0;
                reader.ScrollToCaret();
            }
            finally { DialogKit.SetRedraw(reader, true); }
        };
        FormClosed += (_, _) => { foreach (var f in _fonts) f.Dispose(); };
    }
}

/// <summary>The activity history, newest first: "Today 14:02   Updated DMX Tools v1.1.0 → v1.2.0".</summary>
internal sealed class HistoryDialog : Form
{
    private readonly List<Font> _fonts = new();

    public HistoryDialog(List<ActivityEvent> events, bool dark)
    {
        Text = "Activity";
        DialogKit.Style(this, dark, new Size(620, 480), sizable: true);
        var reader = DialogKit.Reader(dark);
        var buttons = DialogKit.ButtonRow();
        var close = DialogKit.Button("Close", DialogResult.Cancel);
        buttons.Controls.Add(close);
        CancelButton = close;
        Controls.Add(DialogKit.Padded(reader, dark));
        Controls.Add(buttons);
        ResumeLayout(false);
        Load += (_, _) =>
        {
            Font F(float pt, bool semi = false) { var f = Theme.Ui(pt, semi, DeviceDpi); _fonts.Add(f); return f; }
            Font when = F(Theme.PtSmall), what = F(Theme.PtBody);
            if (events.Count == 0)
            {
                DialogKit.Append(reader, "Nothing yet — installs, updates and uninstalls will be listed here.", what, Theme.Sub(dark));
                return;
            }
            reader.SelectionTabs = new[] { Theme.Px(150, DeviceDpi) };
            var now = DateTime.Now;
            bool first = true;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (!first) DialogKit.Append(reader, "\n", what, Theme.Fg(dark));
                first = false;
                DialogKit.Append(reader, ActivityHistory.When(e.At.ToLocalTime().DateTime, now) + "\t", when, Theme.Sub(dark));
                DialogKit.Append(reader, ActivityHistory.Describe(e), what, e.Action == "failed" ? Theme.Danger : Theme.Fg(dark));
            }
            reader.SelectionStart = 0;
            reader.ScrollToCaret();
        };
        FormClosed += (_, _) => { foreach (var f in _fonts) f.Dispose(); };
    }
}

/// <summary>What the app-details window shows (gathered by MainForm; the size is filled in asynchronously).</summary>
internal sealed record AppDetails(
    string Name, string Blurb, string? Category, string? Installed, string? InstalledAt, string? Location,
    string? Latest, string? LatestDate, string? DownloadSize, bool Held, string? Previous,
    string? RunsAs = null);   // ARM64 PCs, when installed: "ARM64" or "x64, through emulation"

/// <summary>Everything about one installed (or installable) app, with "Show in Explorer" and "Release notes…".</summary>
internal sealed class AppDetailsDialog : Form
{
    private readonly Label _size = new();

    public AppDetailsDialog(AppDetails d, bool dark, Func<Task<long>>? sizeOnDisk, Action? showReleaseNotes)
    {
        Text = d.Name;
        DialogKit.Style(this, dark, new Size(520, 400), sizable: false);
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(16, 14, 16, 8),
            BackColor = Theme.Bg(dark),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, string? value, Label? valueLabel = null)
        {
            if (value == null && valueLabel == null) return;
            var l = new Label { Text = label, AutoSize = true, ForeColor = Theme.Muted(dark), Margin = new Padding(0, 4, 14, 4), UseMnemonic = false };
            var v = valueLabel ?? new Label();
            v.Text = value ?? v.Text;
            v.AutoSize = true;
            v.UseMnemonic = false;
            v.MaximumSize = new Size(360, 0);
            v.ForeColor = Theme.Fg(dark);
            v.Margin = new Padding(0, 4, 0, 4);
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(l);
            table.Controls.Add(v);
        }
        Row("App", d.Name);
        Row("About", string.IsNullOrWhiteSpace(d.Blurb) ? null : d.Blurb);
        Row("Section", d.Category);
        Row("Installed", d.Installed ?? "Not installed");
        Row("Installed on", d.InstalledAt);
        Row("Location", d.Location);
        if (d.Location != null) { _size.Text = "Calculating…"; Row("Size on disk", null, _size); }
        Row("Latest", d.Latest);
        Row("Released", d.LatestDate);
        Row("Download size", d.DownloadSize);
        if (d.Held) Row("Updates", "Held at this version — Update All leaves it alone");
        Row("Previous version", d.Previous);
        Row("Runs as", d.RunsAs);

        var buttons = DialogKit.ButtonRow();
        var close = DialogKit.Button("Close", DialogResult.Cancel);
        buttons.Controls.Add(close);
        CancelButton = close;
        if (showReleaseNotes != null)
        {
            var notes = DialogKit.Button("Release notes…");
            notes.Click += (_, _) => showReleaseNotes();
            buttons.Controls.Add(notes);
        }
        if (d.Location != null)
        {
            var reveal = DialogKit.Button("Show in Explorer");
            reveal.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{d.Location}\""); }
                catch (Exception ex) { Log.Write($"show in explorer failed: {ex.Message}"); }
            };
            buttons.Controls.Add(reveal);
        }
        Controls.Add(table);
        Controls.Add(buttons);
        ResumeLayout(false);

        if (sizeOnDisk != null)
        {
            Shown += async (_, _) =>
            {
                long bytes = 0;
                try { bytes = await sizeOnDisk(); } catch { /* shown as 0 */ }
                if (!IsDisposed) _size.Text = ByteSize.Format(bytes);
            };
        }
    }
}

/// <summary>The preview before a setup file is applied: what will install, what's skipped, and whether to also
/// take over the file's list layout.</summary>
internal sealed class ImportPreviewDialog : Form
{
    private readonly CheckBox _layout = new();
    private readonly bool _hasLayout;
    /// <summary>Read after the dialog has closed — so not from <c>_layout.Visible</c>, which is false once the form is hidden.</summary>
    public bool ApplyLayout => _hasLayout && _layout.Checked;

    public ImportPreviewDialog(string summary, bool hasLayout, bool installs, string source, bool dark)
    {
        Text = "Import setup";
        DialogKit.Style(this, dark, new Size(540, 440), sizable: true);
        var text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None, BackColor = Theme.Surface(dark), ForeColor = Theme.Fg(dark),
            Text = $"From {source}\r\n\r\n" + summary.Replace("\n", "\r\n"),
        };
        _layout.Text = "Also use this file's list layout (pinned, hidden and order of apps and sections)";
        _layout.AutoSize = true;
        _hasLayout = hasLayout;
        _layout.Visible = hasLayout;
        _layout.Dock = DockStyle.Bottom;
        _layout.Padding = new Padding(14, 8, 14, 0);
        _layout.UseMnemonic = false;

        var buttons = DialogKit.ButtonRow();
        var cancel = DialogKit.Button("Cancel", DialogResult.Cancel);
        var ok = DialogKit.Button(installs ? "Install" : "Apply", DialogResult.OK, primary: true);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(DialogKit.Padded(text, dark));
        Controls.Add(_layout);
        Controls.Add(buttons);
        ResumeLayout(false);
    }
}
