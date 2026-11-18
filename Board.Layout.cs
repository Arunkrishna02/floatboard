using System.Drawing.Drawing2D;

namespace Floatboard;

sealed partial class Board
{
    readonly Label count = new() { Anchor = AnchorStyles.Right, AutoSize = true };
    readonly Label historyHeading = new() { Text = "Clipboard history", Anchor = AnchorStyles.Left, AutoSize = true };
    readonly Label previewHeading = new() { Text = "Preview", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label empty = new() { Text = "Your clipboard history is empty.\nCopy something in another app to get started.",
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    readonly TableLayoutPanel previewPanel = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
    readonly UsageTile cpuTile = new("CPU");
    readonly UsageTile memoryTile = new("Memory");
    readonly UsageTile gpuTile = new("GPU");
    Color surface, raised, muted, accent, selection;
    Font? rowDetailsFont;

    void BuildLayout()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 54, 63, 36, 0, 83, 43, 23 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var identity = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty
        };
        title.Font = new Font("Segoe UI Semibold", 19);
        title.Margin = Padding.Empty;
        clock.Font = new Font("Segoe UI", 9);
        clock.Margin = new Padding(1, 0, 0, 0);
        identity.Controls.AddRange([title, clock]);
        header.Controls.Add(identity, 0, 0);
        header.Controls.Add(stats, 1, 0);
        header.Controls.Add(options, 2, 0);
        stats.Anchor = options.Anchor = AnchorStyles.None;

        var usage = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 3, 0, 5)
        };
        for (int index = 0; index < 3; index++)
            usage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        usage.Controls.Add(cpuTile, 0, 0);
        usage.Controls.Add(memoryTile, 1, 0);
        usage.Controls.Add(gpuTile, 2, 0);

        var historyBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        historyBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        historyBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        historyHeading.Font = new Font("Segoe UI Semibold", 11);
        historyBar.Controls.Add(historyHeading, 0, 0);
        historyBar.Controls.Add(count, 1, 0);

        var historyPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        historyPanel.Controls.Add(list);
        historyPanel.Controls.Add(empty);
        empty.BringToFront();
        list.ItemHeight = LogicalToDeviceUnits(52);
        list.DrawItem += DrawHistoryRow;
        list.Resize += (_, _) => list.Invalidate();
        DpiChanged += (_, _) => list.ItemHeight = LogicalToDeviceUnits(52);
        rowDetailsFont = new Font(Font.FontFamily, 8.5f);
        Disposed += (_, _) => rowDetailsFont.Dispose();

        previewPanel.Margin = new Padding(0, 3, 0, 4);
        previewPanel.Padding = new Padding(10, 3, 8, 6);
        previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewPanel.Controls.Add(previewHeading, 0, 0);
        previewPanel.Controls.Add(preview, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 3, 0, 0)
        };
        actions.Controls.AddRange([copy, pin, delete]);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(usage, 0, 1);
        layout.Controls.Add(historyBar, 0, 2);
        layout.Controls.Add(historyPanel, 0, 3);
        layout.Controls.Add(previewPanel, 0, 4);
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(status, 0, 6);
        Controls.Add(layout);

        foreach (var button in new[] { stats, options, copy, pin, delete })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.MinimumSize = new Size(0, 29);
            button.Padding = new Padding(8, 2, 8, 2);
            button.Margin = new Padding(0, 0, 6, 0);
            button.UseVisualStyleBackColor = false;
        }
    }

    void DrawHistoryRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= list.Items.Count || list.Items[e.Index] is not HistoryRow row) return;

        e.DrawBackground();
        using (var background = new SolidBrush(BackColor))
            e.Graphics.FillRectangle(background, e.Bounds);

        bool active = (e.State & DrawItemState.Selected) != 0;
        var card = Rectangle.Inflate(e.Bounds, -1, -2);
        card.Width = Math.Max(1, card.Width - 3);
        using var cardBrush = new SolidBrush(active ? selection : surface);
        using var path = RoundedRectangle(card, LogicalToDeviceUnits(8));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(cardBrush, path);
        if (active)
        {
            using var border = new Pen(accent);
            e.Graphics.DrawPath(border, path);
        }

        var content = Rectangle.Inflate(card, -LogicalToDeviceUnits(12), -LogicalToDeviceUnits(5));
        int primaryHeight = content.Height * 3 / 5;
        var primary = new Rectangle(content.X, content.Y, content.Width, primaryHeight);
        var secondary = new Rectangle(content.X, primary.Bottom, content.Width, content.Height - primaryHeight);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, row.Text, Font, primary, ForeColor, flags);
        TextRenderer.DrawText(e.Graphics, row.Details, rowDetailsFont ?? Font, secondary, muted, flags);
        if ((e.State & DrawItemState.Focus) != 0 && list.Focused)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(card, -3, -3), accent, selection);
    }

    static GraphicsPath RoundedRectangle(Rectangle bounds, int corner)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(1, Math.Min(corner * 2, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    void ApplyTheme()
    {
        Opacity = history.Opacity / 100.0;
        bool dark = history.Dark;
        ApplyWindowTheme();
        BackColor = dark ? Color.FromArgb(19, 20, 32) : Color.FromArgb(241, 242, 250);
        ForeColor = dark ? Color.FromArgb(244, 244, 252) : Color.FromArgb(31, 30, 55);
        surface = dark ? Color.FromArgb(32, 34, 51) : Color.White;
        raised = dark ? Color.FromArgb(46, 47, 70) : Color.FromArgb(225, 227, 246);
        muted = dark ? Color.FromArgb(182, 186, 212) : Color.FromArgb(82, 85, 116);
        accent = dark ? Color.FromArgb(180, 171, 255) : Color.FromArgb(77, 60, 176);
        selection = dark ? Color.FromArgb(53, 47, 88) : Color.FromArgb(231, 225, 251);
        title.ForeColor = ForeColor;
        clock.ForeColor = count.ForeColor = status.ForeColor = empty.ForeColor = previewHeading.ForeColor = muted;
        list.BackColor = BackColor;
        list.ForeColor = ForeColor;
        list.Invalidate();
        previewPanel.BackColor = preview.BackColor = surface;
        preview.ForeColor = ForeColor;
        foreach (var tile in new[] { cpuTile, memoryTile, gpuTile })
            tile.ApplyColors(surface, ForeColor, muted, raised, accent);
        foreach (var button in new[] { stats, options, copy, pin, delete })
        {
            button.BackColor = button == copy ? accent : surface;
            button.ForeColor = button == copy ? (dark ? BackColor : Color.White) : ForeColor;
            button.FlatAppearance.MouseOverBackColor = button == copy ? ControlPaint.Light(accent, .15f) : raised;
            button.FlatAppearance.MouseDownBackColor = button == copy ? ControlPaint.Light(accent, .25f) : selection;
        }
        delete.ForeColor = dark ? Color.FromArgb(255, 177, 190) : Color.FromArgb(157, 35, 66);
        var renderer = new ToolStripProfessionalRenderer(new BoardMenuColors(surface, raised, selection))
        {
            RoundedEdges = false
        };
        foreach (var menu in new[] { readings, settings })
        {
            menu.Renderer = renderer;
            ThemeMenu(menu);
        }
    }

    void ApplyWindowTheme()
    {
        if (!IsHandleCreated || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        int dark = history.Dark ? 1 : 0;
        const int immersiveDarkMode = 20;
        DwmSetWindowAttribute(Handle, immersiveDarkMode, ref dark, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    void ThemeMenu(ToolStrip menu)
    {
        menu.BackColor = surface;
        menu.ForeColor = ForeColor;
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = ForeColor;
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                parent.DropDown.Renderer = menu.Renderer;
                ThemeMenu(parent.DropDown);
            }
        }
    }

    sealed class BoardMenuColors(Color surface, Color border, Color selected) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => surface;
        public override Color MenuBorder => border;
        public override Color MenuItemBorder => border;
        public override Color MenuItemSelected => selected;
        public override Color ImageMarginGradientBegin => surface;
        public override Color ImageMarginGradientMiddle => surface;
        public override Color ImageMarginGradientEnd => surface;
        public override Color SeparatorDark => border;
        public override Color SeparatorLight => surface;
    }

    sealed class HistoryRow(Clip clip)
    {
        public Clip Clip { get; } = clip;
        public string Text { get; } = SingleLinePreview(clip.Preview);
        public string Details { get; } =
            $"{(clip.Pinned ? "Pinned  ·  " : "")}{clip.Kind}  ·  {clip.Captured.LocalDateTime:g}";
        public override string ToString() => $"{Text}  ·  {Details}";

        static string SingleLinePreview(string text)
        {
            if (text.Length > 240) text = text[..240] + "…";
            return text.ReplaceLineEndings(" ").Replace('\t', ' ');
        }
    }

    sealed class UsageTile : Panel
    {
        readonly Label name = new() { Anchor = AnchorStyles.Left | AnchorStyles.Top, AutoSize = true };
        readonly Label value = new() { Dock = DockStyle.Right, AutoSize = true, Text = "—" };
        readonly string metric;
        Color track, fill;
        double? usage;

        public UsageTile(string metric)
        {
            this.metric = metric;
            Dock = DockStyle.Fill;
            Margin = new Padding(2, 0, 2, 0);
            Padding = new Padding(9, 7, 9, 0);
            name.Text = metric;
            name.Location = new Point(9, 7);
            name.Font = new Font("Segoe UI", 9);
            value.Font = new Font("Segoe UI Semibold", 10);
            Controls.Add(name);
            Controls.Add(value);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Update(null);
        }

        public void Update(double? percentage)
        {
            string reading = percentage is double number ? $"{number:F0}%" : "—";
            if (value.Text != reading) value.Text = reading;
            AccessibleName = $"{metric}: {Percent(percentage)}";
            if (usage == percentage) return;
            usage = percentage;
            Invalidate();
        }

        public void ApplyColors(Color background, Color foreground, Color secondary, Color track, Color fill)
        {
            BackColor = background;
            ForeColor = foreground;
            name.ForeColor = secondary;
            this.track = track;
            this.fill = fill;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int height = LogicalToDeviceUnits(4);
            int width = Math.Max(0, ClientSize.Width - Padding.Horizontal);
            var bar = new Rectangle(Padding.Left, ClientSize.Height - LogicalToDeviceUnits(11), width, height);
            using var trackBrush = new SolidBrush(track);
            using var fillBrush = new SolidBrush(fill);
            e.Graphics.FillRectangle(trackBrush, bar);
            bar.Width = (int)Math.Round(bar.Width * Math.Clamp(usage ?? 0, 0, 100) / 100);
            e.Graphics.FillRectangle(fillBrush, bar);
        }
    }
}
