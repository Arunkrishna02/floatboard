using System.Runtime.InteropServices;

namespace Floatboard;

sealed class Board : Form
{
    readonly History history = new();
    readonly Metrics metrics = new();
    readonly System.Windows.Forms.Timer pulse = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer capture = new() { Interval = 120 };
    readonly ListView list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly Label title = new() { Text = "Floatboard", AutoSize = true, Font = new Font("Segoe UI Semibold", 17) };
    readonly Label clock = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly TextBox preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly Button stats = new() { Text = "Usage & weather ▾", AutoSize = true };
    readonly Button options = new() { Text = "Settings ▾", AutoSize = true };
    readonly Button copy = new() { Text = "Copy", AutoSize = true };
    readonly Button pin = new() { Text = "Pin", AutoSize = true };
    readonly Button delete = new() { Text = "Delete", AutoSize = true };
    readonly ContextMenuStrip readings = new();
    readonly ContextMenuStrip settings = new();
    readonly NotifyIcon tray = new() { Icon = SystemIcons.Information, Text = "Floatboard" };
    readonly ToolTip help = new();
    uint lastSequence;
    int attempts, tick;
    bool listening, paused, gpuBusy, alerts, overThreshold;
    string weather = "Set a city in Settings";
    int weatherRequest;

    public Board()
    {
        Text = "Floatboard";
        Icon = SystemIcons.Information;
        Font = new Font("Segoe UI", 10);
        TopMost = true;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimumSize = new Size(390, 390);
        Size = new Size(500, 630);
        StartPosition = FormStartPosition.Manual;
        var work = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(Math.Max(work.Left, work.Right - Width - 20), work.Top + 24);
        Height = Math.Min(Height, Math.Max(MinimumSize.Height, work.Height - 48));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var size in new[] { 42, 30, 0, 92, 40, 26 })
            layout.RowStyles.Add(size == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, size));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        title.Anchor = AnchorStyles.Left;
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(stats, 1, 0);
        header.Controls.Add(options, 2, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.AddRange([copy, pin, delete]);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(clock, 0, 1);
        layout.Controls.Add(list, 0, 2);
        layout.Controls.Add(preview, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        layout.Controls.Add(status, 0, 5);
        Controls.Add(layout);

        list.Columns.Add("Clipboard history", 240);
        list.Columns.Add("Format", 77);
        list.Columns.Add("Saved", 95);
        list.ClientSizeChanged += (_, _) => list.Columns[0].Width = Math.Max(90, list.ClientSize.Width - 194);
        list.SelectedIndexChanged += (_, _) => SelectionChanged();
        list.DoubleClick += (_, _) => CopySelected();
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { CopySelected(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.SuppressKeyPress = true; }
        };
        copy.Click += (_, _) => CopySelected();
        pin.Click += (_, _) => TogglePin();
        delete.Click += (_, _) => DeleteSelected();
        stats.Click += (_, _) => readings.Show(stats, new Point(0, stats.Height));
        options.Click += (_, _) => settings.Show(options, new Point(0, options.Height));
        for (int i = 0; i < 5; i++) readings.Items.Add(new ToolStripMenuItem { AutoSize = true });
        readings.Items.Add(new ToolStripSeparator());
        var alert = new ToolStripMenuItem("Notify when CPU, memory or GPU reaches 90%") { CheckOnClick = true };
        alert.CheckedChanged += (_, _) => { alerts = alert.Checked; overThreshold = false; };
        readings.Items.Add(alert);

        settings.Items.Add("Switch light / dark theme", null, (_, _) =>
        {
            history.Dark = !history.Dark;
            ApplyTheme();
            Save();
        });
        var opacity = new ToolStripMenuItem("Opacity");
        foreach (int value in new[] { 70, 80, 92, 100 })
            opacity.DropDownItems.Add($"{value}%", null, (_, _) => { history.Opacity = value; ApplyTheme(); Save(); });
        settings.Items.Add(opacity);
        settings.Items.Add("Set weather city…", null, (_, _) => SetCity());
        settings.Items.Add("Refresh weather", null, async (_, _) => await UpdateWeather());
        var pause = new ToolStripMenuItem("Pause automatic capture") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            paused = pause.Checked;
            lastSequence = GetClipboardSequenceNumber();
            capture.Stop();
            SetStatus(paused ? "Automatic capture paused." : "Automatic capture resumed.");
        };
        settings.Items.Add(pause);
        settings.Items.Add("Save current clipboard", null, (_, _) => CaptureClipboard(true));
        settings.Items.Add("Hide to tray", null, (_, _) => Hide());

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show Floatboard", null, (_, _) => ShowBoard());
        trayMenu.Items.Add("Exit Floatboard", null, (_, _) => Close());
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowBoard();
        tray.Visible = true;
        help.SetToolTip(copy, "Restore the selected entry to the Windows clipboard, then paste with Ctrl+V.");
        help.SetToolTip(stats, "CPU, memory, GPU, network throughput, and weather");

        capture.Tick += (_, _) => CaptureClipboard(false);
        pulse.Tick += async (_, _) =>
        {
            UpdateReadings();
            if (GetClipboardSequenceNumber() != lastSequence && !paused && !capture.Enabled)
                QueueCapture();
            tick++;
            if (tick % 8 == 0) await UpdateGpu();
            if (tick % 900 == 0) await UpdateWeather();
        };
        ApplyTheme();
        RenderHistory();
        SetStatus(history.LoadError ?? "Copy in any app to start saving items.");
        Shown += async (_, _) =>
        {
            lastSequence = GetClipboardSequenceNumber();
            UpdateReadings();
            pulse.Start();
            await Task.WhenAll(UpdateGpu(), UpdateWeather());
        };
        FormClosed += (_, _) =>
        {
            pulse.Dispose();
            capture.Dispose();
            tray.Dispose();
            trayMenu.Dispose();
            settings.Dispose();
            readings.Dispose();
            help.Dispose();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        listening = AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (listening) RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x031D && !paused && GetClipboardSequenceNumber() != lastSequence)
            QueueCapture();
        base.WndProc(ref message);
    }

    void QueueCapture()
    {
        attempts = 0;
        capture.Start();
    }

    void CaptureClipboard(bool manual)
    {
        uint sequence = GetClipboardSequenceNumber();
        if (!manual && (paused || sequence == lastSequence))
        {
            capture.Stop();
            return;
        }
        try
        {
            var clip = Clip.Read(out string? reason);
            if (sequence != GetClipboardSequenceNumber() && !manual) return;
            capture.Stop();
            lastSequence = sequence;
            if (clip == null)
            {
                if (reason != null || manual) SetStatus(reason ?? "The clipboard is empty.");
                return;
            }
            if (!history.Add(clip))
            {
                SetStatus("History is full. Unpin or delete an item to make room.");
                return;
            }
            RenderHistory();
            SetStatus($"Saved to history · {history.Items.Count}/{History.Limit}");
            Save();
        }
        catch (Exception e) when (e is ExternalException or ArgumentException or InvalidOperationException)
        {
            if (!manual && ++attempts < 8) return;
            capture.Stop();
            lastSequence = sequence;
            SetStatus("Could not read the clipboard. Try copying again.");
        }
    }

    Clip? Selected => list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as Clip;

    void RenderHistory(Clip? selected = null)
    {
        selected ??= Selected;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var clip in history.Items.OrderByDescending(item => item.Pinned))
        {
            string text = clip.Preview.ReplaceLineEndings(" ").Replace('\t', ' ');
            if (text.Length > 180) text = text[..180] + "…";
            var row = new ListViewItem([clip.Pinned ? "★  " + text : text, clip.Kind,
                clip.Captured.LocalDateTime.ToString("g")]) { Tag = clip };
            list.Items.Add(row);
            if (ReferenceEquals(clip, selected)) row.Selected = true;
        }
        list.EndUpdate();
        SelectionChanged();
    }

    void SelectionChanged()
    {
        var clip = Selected;
        copy.Enabled = pin.Enabled = delete.Enabled = clip != null;
        pin.Text = clip?.Pinned == true ? "Unpin" : "Pin";
        preview.Text = clip?.Files != null ? string.Join(Environment.NewLine, clip.Files)
            : clip?.Text ?? clip?.Preview ?? "";
    }

    void CopySelected()
    {
        if (Selected is not { } clip) return;
        try
        {
            clip.Restore();
            lastSequence = GetClipboardSequenceNumber();
            capture.Stop();
            SetStatus("Copied. Switch to your app and press Ctrl+V.");
        }
        catch (Exception e) when (e is ExternalException or ArgumentException)
        {
            SetStatus("Could not restore this item. Try again.");
        }
    }

    void TogglePin()
    {
        if (Selected is not { } clip) return;
        clip.Pinned = !clip.Pinned;
        RenderHistory(clip);
        SetStatus(clip.Pinned ? "Pinned to the top." : "Unpinned.");
        Save();
    }

    void DeleteSelected()
    {
        if (Selected is not { } clip) return;
        history.Items.Remove(clip);
        RenderHistory();
        SetStatus($"Deleted from history · {history.Items.Count}/{History.Limit}");
        Save();
    }

    void SetCity()
    {
        using var dialog = new Form { Text = "Weather city", Font = Font, ClientSize = new Size(330, 116),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false };
        var label = new Label { Text = "Enter a city. Leave blank to turn weather off.", AutoSize = true, Location = new Point(12, 10) };
        var input = new TextBox { Text = history.City, MaxLength = 100, Bounds = new Rectangle(12, 36, 304, 28) };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Bounds = new Rectangle(154, 74, 78, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(238, 74, 78, 30) };
        dialog.Controls.AddRange([label, input, save, cancel]);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        history.City = input.Text.Trim();
        Save();
        _ = UpdateWeather();
    }

    async Task UpdateWeather()
    {
        int request = ++weatherRequest;
        string city = history.City;
        weather = city.Length == 0 ? "Set a city in Settings" : $"Updating · {city}";
        string result = city.Length == 0 ? weather : await Weather.Read(city);
        if (IsDisposed || request != weatherRequest) return;
        weather = result;
        UpdateMenu();
    }

    async Task UpdateGpu()
    {
        if (gpuBusy) return;
        gpuBusy = true;
        try { await metrics.SampleGpu(); }
        finally { gpuBusy = false; }
        if (!IsDisposed) UpdateMenu();
    }

    void UpdateReadings()
    {
        var now = DateTime.Now;
        clock.Text = $"{now:t}  •  {now:ddd, d MMM yyyy}";
        metrics.Sample();
        UpdateMenu();
        bool high = metrics.Cpu >= 90 || metrics.Ram >= 90 || metrics.Gpu >= 90;
        if (alerts && high && !overThreshold)
            tray.ShowBalloonTip(4000, "High system usage",
                $"CPU {Percent(metrics.Cpu)} · Memory {Percent(metrics.Ram)} · GPU {Percent(metrics.Gpu)}",
                ToolTipIcon.Info);
        overThreshold = high;
    }

    void UpdateMenu()
    {
        readings.Items[0].Text = $"CPU   {Percent(metrics.Cpu)}";
        readings.Items[1].Text = $"Memory   {metrics.Memory} · {Percent(metrics.Ram)}";
        readings.Items[2].Text = $"GPU   {Percent(metrics.Gpu)}";
        readings.Items[3].Text = $"Network   {metrics.Network}";
        readings.Items[4].Text = $"Weather   {weather}";
    }

    static string Percent(double? value) => value is double percent ? $"{percent:F0}%" : "Unavailable";

    void ApplyTheme()
    {
        Opacity = history.Opacity / 100.0;
        BackColor = history.Dark ? Color.FromArgb(18, 25, 39) : Color.FromArgb(238, 244, 249);
        ForeColor = history.Dark ? Color.FromArgb(234, 241, 252) : Color.FromArgb(22, 39, 59);
        var surface = history.Dark ? Color.FromArgb(29, 41, 59) : Color.White;
        title.ForeColor = history.Dark ? Color.FromArgb(131, 227, 205) : Color.FromArgb(0, 99, 83);
        foreach (Control control in new Control[] { list, preview, stats, options, copy, pin, delete })
        {
            control.BackColor = surface;
            control.ForeColor = ForeColor;
            if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
            }
        }
    }

    void ShowBoard()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void Save()
    {
        try { history.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("Could not save history to disk.");
        }
    }

    void SetStatus(string text)
    {
        status.Text = text;
        help.SetToolTip(status, text);
    }

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr window);
}
