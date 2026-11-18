using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Floatboard;

sealed class Clip
{
    public string? Text { get; set; }
    public string? Html { get; set; }
    public string? Rtf { get; set; }
    public byte[]? Png { get; set; }
    public string[]? Files { get; set; }
    public DateTimeOffset Captured { get; set; } = DateTimeOffset.Now;
    public bool Pinned { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Preview => !string.IsNullOrWhiteSpace(Text) ? Text.Trim()
        : Files is { Length: > 0 } ? string.Join(", ", Files.Select(Path.GetFileName))
        : Png != null ? "Image" : "Formatted content";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Kind => Files != null ? "Files" : Png != null ? "Image"
        : Html != null || Rtf != null ? "Rich text" : "Text";

    public bool Matches(Clip other) => Text == other.Text && Html == other.Html && Rtf == other.Rtf
        && (Png ?? []).AsSpan().SequenceEqual(other.Png ?? [])
        && (Files ?? []).SequenceEqual(other.Files ?? []);

    public static Clip? Read(out string? reason)
    {
        reason = null;
        var data = Clipboard.GetDataObject();
        if (data == null) return null;
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false))
        {
            reason = "Skipped content marked private by its source.";
            return null;
        }
        if (ClipboardFlags.ExcludesHistory())
        {
            reason = "Skipped content marked private by its source.";
            return null;
        }

        var clip = new Clip
        {
            Text = data.GetData(DataFormats.UnicodeText) as string,
            Html = data.GetData(DataFormats.Html, false) as string,
            Rtf = data.GetData(DataFormats.Rtf, false) as string,
            Files = data.GetData(DataFormats.FileDrop, false) as string[]
        };
        if (clip.Files == null && Clipboard.ContainsImage())
        {
            using var image = Clipboard.GetImage();
            if (image != null)
            {
                if ((long)image.Width * image.Height > 24_000_000)
                {
                    reason = "Image is too large for clipboard history.";
                    return null;
                }
                using var png = new MemoryStream();
                image.Save(png, ImageFormat.Png);
                clip.Png = png.ToArray();
            }
        }
        if (clip.Text == null && clip.Html == null && clip.Rtf == null && clip.Png == null && clip.Files == null)
        {
            reason = "This clipboard format is unavailable in Floatboard.";
            return null;
        }
        long size = 3L * ((clip.Text?.Length ?? 0) + (clip.Html?.Length ?? 0) + (clip.Rtf?.Length ?? 0))
            + (clip.Png?.LongLength ?? 0) + 3L * (clip.Files?.Sum(path => path.Length) ?? 0);
        if (size <= 1_048_576) return clip;
        reason = "This item exceeds the 1 MB history limit.";
        return null;
    }

    public void Restore()
    {
        var data = new DataObject();
        if (Text != null) data.SetData(DataFormats.UnicodeText, true, Text);
        if (Html != null) data.SetData(DataFormats.Html, false, Html);
        if (Rtf != null) data.SetData(DataFormats.Rtf, false, Rtf);
        if (Files != null) data.SetData(DataFormats.FileDrop, true, Files);
        using var stream = Png != null ? new MemoryStream(Png) : null;
        using var image = stream != null ? Image.FromStream(stream) : null;
        using var bitmap = image != null ? new Bitmap(image) : null;
        if (bitmap != null) data.SetImage(bitmap);
        Clipboard.SetDataObject(data, true, 5, 40);
    }
}

static class ClipboardFlags
{
    public static bool ExcludesHistory()
    {
        uint format = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        if (format == 0 || !IsClipboardFormatAvailable(format)) return false;
        if (!OpenClipboard(IntPtr.Zero))
            throw new ExternalException("Could not check the clipboard history flag.");
        try
        {
            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero || GlobalSize(handle).ToUInt64() < 4) return false;
            IntPtr address = GlobalLock(handle);
            if (address == IntPtr.Zero) return false;
            try { return Marshal.ReadInt32(address) == 0; }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
}

sealed class History
{
    public const int Limit = 200;
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Floatboard");
    static readonly string FilePath = Path.Combine(Folder, "history.json");
    public List<Clip> Items { get; } = [];
    public string? LoadError { get; }
    public bool Dark { get; set; } = true;
    public int Opacity { get; set; } = 92;
    public string City { get; set; } = "";

    public History()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllBytes(FilePath));
            if (saved == null) return;
            Items.AddRange(saved.Items.Take(Limit));
            Dark = saved.Dark;
            Opacity = Math.Clamp(saved.Opacity, 68, 100);
            City = saved.City;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = "Saved history could not be opened.";
        }
    }

    public bool Add(Clip clip)
    {
        int index = Items.FindIndex(clip.Matches);
        if (index >= 0)
        {
            clip.Pinned = Items[index].Pinned;
            Items.RemoveAt(index);
        }
        else if (Items.Count >= Limit)
        {
            int oldest = Items.FindLastIndex(item => !item.Pinned);
            if (oldest < 0) return false;
            Items.RemoveAt(oldest);
        }
        Items.Insert(0, clip);
        return true;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, new Snapshot(Items, Dark, Opacity, City));
        File.Move(temp, FilePath, true);
    }

    sealed record Snapshot(List<Clip> Items, bool Dark, int Opacity, string City);
}
