using System.Runtime.InteropServices;

namespace Floatboard;

sealed partial class Board : Form
{
    readonly History history = new();
    readonly Metrics metrics = new();
    readonly System.Windows.Forms.Timer pulse = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer capture = new() { Interval = 120 };
    readonly ListBox list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed,
        BorderStyle = BorderStyle.None, IntegralHeight = false, AccessibleName = "Clipboard history" };
    readonly Label title = new() { Text = "Floatboard", AutoSize = true };
    readonly Label clock = new() { AutoSize = true };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly TextBox preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, AccessibleName = "Clipboard item preview" };
    readonly Button stats = new() { Text = "Usage & weather ▾", AutoSize = true };
    readonly Button options = new() { Text = "Settings ▾", AutoSize = true };
    readonly Button copy = new() { Text = "Copy item", AutoSize = true };
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
        MinimumSize = new Size(430, 460);
        Size = new Size(500, 650);
        StartPosition = FormStartPosition.Manual;
        var work = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(Math.Max(work.Left, work.Right - Width - 20), work.Top + 24);
        Height = Math.Min(Height, Math.Max(MinimumSize.Height, work.Height - 48));

        BuildLayout();
        list.SelectedIndexChanged += (_, _) => SelectionChanged();
        list.MouseDoubleClick += (_, e) =>
        {
            if (list.IndexFromPoint(e.Location) >= 0) CopySelected();
        };
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
        ApplyWindowTheme();
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

    Clip? Selected => (list.SelectedItem as HistoryRow)?.Clip;

    void RenderHistory(Clip? selected = null)
    {
        var previous = Selected;
        selected ??= previous;
        int oldIndex = list.SelectedIndex;
        var top = list.Items.Count > 0 && list.TopIndex > 0
            ? (list.Items[list.TopIndex] as HistoryRow)?.Clip : null;
        var rows = history.Items.OrderByDescending(item => item.Pinned).Select(clip => new HistoryRow(clip)).ToArray();
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            list.Items.AddRange(rows);
            int index = Array.FindIndex(rows, row => ReferenceEquals(row.Clip, selected));
            if (index < 0 && rows.Length > 0)
                index = Math.Clamp(oldIndex, 0, rows.Length - 1);
            list.SelectedIndex = index;
            int topIndex = top == null ? -1 : Array.FindIndex(rows, row => ReferenceEquals(row.Clip, top));
            if (topIndex >= 0 && selected == previous && index >= topIndex
                && index < topIndex + Math.Max(1, list.ClientSize.Height / list.ItemHeight))
                list.TopIndex = topIndex;
            if (selected?.Pinned == true && index >= 0 && index < list.TopIndex)
                list.TopIndex = index;
        }
        finally { list.EndUpdate(); }
        count.Text = history.Items.Count == 1 ? "1 item" : $"{history.Items.Count} items";
        empty.Visible = rows.Length == 0;
        SelectionChanged();
    }

    void SelectionChanged()
    {
        var clip = Selected;
        copy.Enabled = pin.Enabled = delete.Enabled = clip != null;
        pin.Text = clip?.Pinned == true ? "Unpin" : "Pin";
        previewHeading.Text = clip == null ? "Preview" : $"Preview  ·  {clip.Kind}";
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
        cpuTile.Update(metrics.Cpu);
        memoryTile.Update(metrics.Ram);
        gpuTile.Update(metrics.Gpu);
        readings.Items[0].Text = $"CPU   {Percent(metrics.Cpu)}";
        readings.Items[1].Text = $"Memory   {metrics.Memory} · {Percent(metrics.Ram)}";
        readings.Items[2].Text = $"GPU   {Percent(metrics.Gpu)}";
        readings.Items[3].Text = $"Network   {metrics.Network}";
        readings.Items[4].Text = $"Weather   {weather}";
    }

    static string Percent(double? value) => value is double percent ? $"{percent:F0}%" : "Unavailable";

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
