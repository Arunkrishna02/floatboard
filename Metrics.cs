using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Floatboard;

sealed class Metrics
{
    ulong idle, total;
    long netTime;
    Dictionary<string, (long down, long up)> interfaces = [];
    public double? Cpu { get; private set; }
    public double? Ram { get; private set; }
    public string Memory { get; private set; } = "Unavailable";
    public string Network { get; private set; } = "Sampling…";
    public double? Gpu { get; private set; }

    public void Sample()
    {
        if (GetSystemTimes(out ulong nextIdle, out ulong kernel, out ulong user))
        {
            ulong nextTotal = kernel + user;
            if (total > 0 && nextTotal > total)
                Cpu = Math.Clamp(100 * (1 - (double)(nextIdle - idle) / (nextTotal - total)), 0, 100);
            (idle, total) = (nextIdle, nextTotal);
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0)
        {
            ulong used = memory.TotalPhysical - memory.AvailablePhysical;
            Ram = 100.0 * used / memory.TotalPhysical;
            Memory = $"{used / 1073741824.0:F1} / {memory.TotalPhysical / 1073741824.0:F1} GB";
        }

        try
        {
            var next = new Dictionary<string, (long down, long up)>();
            long down = 0, up = 0;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(a => a.OperationalStatus == OperationalStatus.Up
                             && a.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
            {
                try
                {
                    var stats = adapter.GetIPStatistics();
                    next[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
                    if (interfaces.TryGetValue(adapter.Id, out var previous))
                    {
                        down += Math.Max(0, stats.BytesReceived - previous.down);
                        up += Math.Max(0, stats.BytesSent - previous.up);
                    }
                }
                catch (NetworkInformationException) { }
            }
            long now = Stopwatch.GetTimestamp();
            if (netTime != 0)
            {
                double seconds = Stopwatch.GetElapsedTime(netTime, now).TotalSeconds;
                Network = next.Count == 0 ? "Offline" : $"↓ {Speed(down / seconds)}   ↑ {Speed(up / seconds)}";
            }
            (interfaces, netTime) = (next, now);
        }
        catch (NetworkInformationException) { Network = "Unavailable"; }
    }

    public async Task SampleGpu()
    {
        const string command = "$ErrorActionPreference='Stop'; $engines=Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine; if ($engines) { $load=$engines | Group-Object { $_.Name -replace '^pid_\\d+_', '' } | ForEach-Object { ($_.Group | Measure-Object -Property UtilizationPercentage -Sum).Sum } | Measure-Object -Maximum; [Console]::WriteLine($load.Maximum.ToString([Globalization.CultureInfo]::InvariantCulture)) }";
        try
        {
            var options = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
                options.ArgumentList.Add(argument);
            using var process = Process.Start(options);
            if (process == null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                string value = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Gpu = process.ExitCode == 0 && double.TryParse(value, CultureInfo.InvariantCulture, out double percent)
                    ? Math.Clamp(percent, 0, 100) : null;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                Gpu = null;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Gpu = null;
        }
    }

    static string Speed(double bytes) => bytes >= 125_000
        ? $"{bytes * 8 / 1_000_000:F1} Mbps" : $"{bytes * 8 / 1_000:F0} kbps";

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
