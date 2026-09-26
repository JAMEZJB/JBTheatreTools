using System.Globalization;

namespace JBTheatreTools;

/// <summary>The launcher's message box, in place of the stock MessageBox and TaskDialog (a white box in dark mode):
/// the message on the Surface token beside a small semantic glyph, the house buttons on a Ground footer under a Line
/// hairline, a dark title bar in dark mode, Inter throughout. Same call shape and results as MessageBox.Show, so the call
/// sites read as before: the first button is the default (Enter), and Esc or the close box give the "no" answer (No /
/// Cancel / OK) — a stray keystroke never confirms.</summary>
internal static class HouseMessage
{
    public static DialogResult Show(IWin32Window? owner, string text, string caption,
                                    MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        (string, DialogResult)[] set = buttons switch
        {
            MessageBoxButtons.YesNo => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No) },
            MessageBoxButtons.YesNoCancel => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.OKCancel => new[] { ("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.RetryCancel => new[] { ("Retry", DialogResult.Retry), ("Cancel", DialogResult.Cancel) },
            _ => new[] { ("OK", DialogResult.OK) },
        };
        var cancel = buttons switch
        {
            MessageBoxButtons.YesNo => DialogResult.No,
            MessageBoxButtons.OK => DialogResult.OK,
            _ => DialogResult.Cancel,
        };
        return Ask(owner, caption, text, set, set[0].Item2, cancel, icon);
    }

    /// <param name="choices">Label + result, in left-to-right order.</param>
    /// <param name="defaultResult">The button Enter presses — drawn as the accent-filled primary.</param>
    /// <param name="cancelResult">What Esc and the close box answer.</param>
    public static DialogResult Ask(IWin32Window? owner, string caption, string text, (string Label, DialogResult Result)[] choices,
                                   DialogResult defaultResult, DialogResult cancelResult, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        var ownerControl = owner as Control ?? (owner != null ? Control.FromHandle(owner.Handle) : null);
        int dpi = ownerControl?.DeviceDpi ?? 96;
        using var form = new MessageForm(caption, text, choices, defaultResult, cancelResult, icon, dpi, Theme.CurrentDark);
        // An owner that isn't on screen (the window hidden to the notification area, or not shown yet) can't host it.
        if (ownerControl is { IsHandleCreated: true, Visible: true })
            return form.ShowDialog(owner);
        // Hidden owner: the question must still be findable (taskbar button, on top), and the hidden window must not come
        // back usable underneath it when restored from the tray (the modal loop only disables VISIBLE windows).
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ShowInTaskbar = true;
        form.TopMost = true;
        bool wasEnabled = ownerControl?.Enabled ?? false;
        if (ownerControl != null) ownerControl.Enabled = false;
        try { return form.ShowDialog(); }
        finally { if (ownerControl is { IsDisposed: false }) ownerControl.Enabled = wasEnabled; }
    }

    private sealed class MessageForm : Form
    {
        private readonly List<Font> _fonts = new();
        private readonly DialogResult _cancelResult;
        private readonly int _dpi;
        private int S(int v) => Theme.Px(v, _dpi);

        public MessageForm(string caption, string text, (string Label, DialogResult Result)[] choices,
                           DialogResult defaultResult, DialogResult cancelResult, MessageBoxIcon icon, int dpi, bool dark)
        {
            _dpi = dpi;
            _cancelResult = cancelResult;
            Text = caption;
            AutoScaleMode = AutoScaleMode.None;   // laid out below in device pixels for the owner's DPI
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Surface(dark);
            ForeColor = Theme.Fg(dark);
            Font F(float pt, HouseWeight w = HouseWeight.Regular) { var f = Theme.Ui(pt, w, dpi); _fonts.Add(f); return f; }
            Font = F(Theme.PtBody);

            // The semantic glyph (the MessageBox icon's meaning, in the house colours).
            (string glyph, Color tint)? mark = icon switch
            {
                MessageBoxIcon.Error => ("!", Theme.Danger),
                MessageBoxIcon.Warning => ("!", Theme.Warn),
                MessageBoxIcon.Question => ("?", Theme.Info),
                MessageBoxIcon.Information => ("i", Theme.Info),
                _ => null,
            };
            int pad = S(20), glyph = S(28), gap = S(14);
            int textLeft = pad + (mark != null ? glyph + gap : 0);
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
            int oneLine = TextRenderer.MeasureText(text, Font, Size.Empty, flags & ~TextFormatFlags.WordBreak).Width;
            // A list (a batch summary) gets more width so each item stays on one line, as the stock box did.
            bool isList = text.Count(c => c == '\n') >= 4;
            int textW = Math.Clamp(oneLine + S(4), S(260), isList ? S(620) : S(400));
            var measured = TextRenderer.MeasureText(text, Font, new Size(textW, int.MaxValue), flags);
            // Never taller than the screen allows: past ~60 % of the working area the text scrolls (and stays copyable).
            int maxTextH = (int)(Screen.FromPoint(Cursor.Position).WorkingArea.Height * 0.6);
            Control message = measured.Height <= maxTextH
                ? new Label
                {
                    Text = text, AutoSize = false, UseMnemonic = false, ForeColor = Theme.Fg(dark),
                    Bounds = new Rectangle(textLeft, pad, textW, measured.Height),
                }
                : new TextBox
                {
                    Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Multiline = true, ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, TabStop = false,
                    BackColor = Theme.Surface(dark), ForeColor = Theme.Fg(dark),
                    Bounds = new Rectangle(textLeft, pad, textW + S(18), maxTextH),
                };
            if (message is TextBox tb) tb.HandleCreated += (_, _) => HouseDraw.NativeTheme(tb, dark);
            // Screen readers announce the message with the dialog, and Ctrl+C copies it (both as the stock box).
            AccessibleRole = AccessibleRole.Dialog;
            AccessibleDescription = text;
            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C && message is not TextBox { SelectionLength: > 0 })
                {
                    try { Clipboard.SetText($"{caption}\r\n\r\n{text}"); } catch { /* clipboard busy */ }
                    e.Handled = true;
                }
            };
            int bodyH = Math.Max(message.Bottom, pad + (mark != null ? glyph : 0)) + pad;
            if (mark is { } m)
            {
                var badge = new MessageGlyph(m.glyph, m.tint, F(Theme.PtBody, HouseWeight.SemiBold))
                    { Bounds = new Rectangle(pad, pad - S(3), glyph, glyph) };
                Controls.Add(badge);
            }
            Controls.Add(message);

            // Footer: Ground, a Line hairline on top, the buttons right-aligned in the given order.
            var footer = new Panel { BackColor = Theme.Bg(dark) };
            footer.Paint += (_, e) => { using var pen = new Pen(Theme.Line(dark)); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var buttons = new List<HouseButton>();
            foreach (var (label, result) in choices)
            {
                var b = new HouseButton(result == defaultResult ? HouseRole.Primary : HouseRole.Secondary)
                    { Text = label, AutoSize = true, DialogResult = result };
                // Measured now (an AutoSize button only sizes itself on its first layout); short labels ("OK", "No")
                // get a comfortable 76 px target.
                var pref = b.GetPreferredSize(Size.Empty);
                b.AutoSize = false;
                b.Size = new Size(Math.Max(pref.Width, S(76)), pref.Height);
                buttons.Add(b);
                footer.Controls.Add(b);
                if (result == defaultResult) AcceptButton = b;
                if (result == cancelResult) CancelButton = b;
            }
            int buttonsW = buttons.Sum(b => b.Width) + S(8) * (buttons.Count - 1);
            int footerH = (buttons.Count > 0 ? buttons.Max(b => b.Height) : S(26)) + 2 * S(12);
            int width = Math.Max(message.Right + pad, buttonsW + 2 * pad);
            ClientSize = new Size(width, bodyH + footerH);
            footer.Bounds = new Rectangle(0, bodyH, width, footerH);
            int x = width - pad;
            for (int i = buttons.Count - 1; i >= 0; i--)
            {
                var b = buttons[i];
                b.Location = new Point(x - b.Width, (footerH - b.Height) / 2);
                x = b.Left - S(8);
            }
            // Buttons first in the tab order, the default one focused (as the stock box does).
            for (int i = 0; i < buttons.Count; i++) buttons[i].TabIndex = i;
            Controls.Add(footer);
            ActiveControl = buttons.FirstOrDefault(b => b.DialogResult == defaultResult);

            HandleCreated += (_, _) => Theme.ApplyTitleBar(this, dark);
            Shown += (_, _) =>
            {
                Theme.ApplyTitleBar(this, dark);
                (icon switch
                {
                    MessageBoxIcon.Error => System.Media.SystemSounds.Hand,
                    MessageBoxIcon.Warning => System.Media.SystemSounds.Exclamation,
                    MessageBoxIcon.Information => System.Media.SystemSounds.Asterisk,
                    _ => null,
                })?.Play();
            };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The close box reports Cancel: give the caller the dialog's own "no" answer instead (No, for Yes/No).
            if (DialogResult is DialogResult.Cancel or DialogResult.None) DialogResult = _cancelResult;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { foreach (var f in _fonts) f.Dispose(); _fonts.Clear(); }
        }
    }

    /// <summary>A round badge: a 15% wash of the semantic colour with the glyph in that colour.</summary>
    private sealed class MessageGlyph : Control
    {
        private readonly string _glyph;
        private readonly Color _tint;
        private readonly Font _font;

        public MessageGlyph(string glyph, Color tint, Font font)
        {
            _glyph = glyph; _tint = tint; _font = font;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var back = HouseDraw.EffectiveBack(this);
            g.Clear(back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(Theme.Blend(_tint, back, 0.15))) g.FillEllipse(fill, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, _glyph, _font, ClientRectangle, _tint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }
}

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
        f.ShowIcon = false;   // no stock WinForms icon in the title bar (Settings and the message box show none either)
        f.FormBorderStyle = sizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
        if (sizable) f.MinimumSize = new Size(logicalClientSize.Width / 2, logicalClientSize.Height / 2);
        f.BackColor = Theme.Bg(dark);
        f.ForeColor = Theme.Fg(dark);
        var font = Theme.Ui(Theme.PtBody);
        f.Font = font;
        f.HandleCreated += (_, _) => Theme.ApplyTitleBar(f, dark);
        f.Shown += (_, _) => Theme.ApplyTitleBar(f, dark);   // again once visible: Windows 11 (26100) can miss the first
        f.FormClosed += (_, _) => font.Dispose();
    }

    /// <summary>The one confirmation behind every way of turning show lock OFF (the banner, the More menu, Ctrl+L, the
    /// Settings checkbox). Keep On is the default (Enter) and what Esc or the close box answer: turning the lock off
    /// mid-show takes a deliberate click. True = turn it off.</summary>
    public static bool ConfirmTurnOffShowLock(IWin32Window owner) =>
        // Esc and the close box answer Keep On too (the house message box's cancel result), i.e. not "Turn Off".
        HouseMessage.Ask(owner, "Turn Off Show Lock?",
            "Installs, updates and uninstalls can run again, including automatic updates if they're switched on.",
            new[] { ("Keep On", DialogResult.Cancel), ("Turn Off", DialogResult.OK) },
            defaultResult: DialogResult.Cancel, cancelResult: DialogResult.Cancel, MessageBoxIcon.Question) == DialogResult.OK;

    /// <summary>"Reinstall … as the x64 / ARM64 build?" (the row's ⋯ → "Use the x64 build (emulated)"). Cancel is the
    /// default (Enter) and what Esc or the close box answer. True = reinstall.</summary>
    public static bool ConfirmReinstall(IWin32Window owner, string caption, string text) =>
        HouseMessage.Ask(owner, caption, text,
            new[] { ("Reinstall", DialogResult.OK), ("Cancel", DialogResult.Cancel) },
            defaultResult: DialogResult.Cancel, cancelResult: DialogResult.Cancel, MessageBoxIcon.Question) == DialogResult.OK;

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
            Padding = new Padding(14, 10, 14, 14),
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

    /// <summary>Wraps a control in the house panel the Settings window uses — a rounded, radius-8 Surface panel with a
    /// Line hairline, inset from the window edge — so every secondary window reads the same.</summary>
    public static Panel Padded(Control inner, bool dark)
    {
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 14, 14, 0), BackColor = Theme.Bg(dark) };
        var panel = new HousePanel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 6, 12), BackColor = Theme.Surface(dark) };
        inner.BackColor = Theme.Surface(dark);
        panel.Controls.Add(inner);
        host.Controls.Add(panel);
        return host;
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
        // Focus starts on Close, not the reader (a read-only box with focus shows a blinking caret). On Load: the
        // button has to be on the form before it can be made active.
        Load += (_, _) => ActiveControl = close;
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
        // Focus starts on Close, not the reader (a read-only box with focus shows a blinking caret). On Load: the
        // button has to be on the form before it can be made active.
        Load += (_, _) => ActiveControl = close;
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
        DialogKit.Style(this, dark, new Size(540, 460), sizable: false);
        // The table hugs its rows (docked to the top, auto-sized) inside a scrolling panel, which shows a scrollbar only
        // when the rows are taller than the window — a scrolling TableLayoutPanel of its own showed one regardless.
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
            Padding = new Padding(2, 2, 8, 2),
        };
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroller.Controls.Add(table);
        scroller.HandleCreated += (_, _) => HouseDraw.NativeTheme(scroller, dark);   // a dark scrollbar if it ever scrolls
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
            v.MaximumSize = new Size(350, 0);
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
        Controls.Add(DialogKit.Padded(scroller, dark));
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
    private readonly HouseCheckBox _layout = new();
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
        _hasLayout = hasLayout;
        _layout.Visible = hasLayout;
        _layout.Dock = DockStyle.Bottom;
        _layout.Padding = new Padding(16, 12, 14, 0);
        _layout.BackColor = Theme.Bg(dark);
        // The house check box wraps to the window's width; its height follows (docked Bottom, so only the height is ours).
        _layout.Resize += (_, _) =>
        {
            int h = _layout.GetPreferredSize(new Size(_layout.Width, 0)).Height;
            if (_layout.Height != h) _layout.Height = h;
        };

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
