using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using MonitorTool.Models;

namespace MonitorTool.Services;

/// <summary>
/// Collects CPU, memory, GPU, and temperature metrics asynchronously.
/// All data sources are initialised once and reused to minimise overhead.
/// </summary>
public sealed class SystemMetricsService : IDisposable
{
    // ── Win32 memory query ───────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ── Fields ───────────────────────────────────────────────────────────────
    private readonly PerformanceCounter? _cpuCounter;
    private ManagementObjectSearcher? _gpuSearcher;
    private ManagementObjectSearcher? _tempSearcher;
    private bool _disposed;

    // Represents 273.2 K (0 °C) in the tenths-of-Kelvin unit used by
    // MSAcpi_ThermalZoneTemperature (i.e. 2732 = 273.2 × 10).
    private const double KelvinOffsetTenths = 2732.0;

    // ── Constructor ──────────────────────────────────────────────────────────
    public SystemMetricsService()
    {
        // CPU performance counter – first call returns 0 (baseline), subsequent
        // calls return the real usage.
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue(); // discard baseline reading
        }
        catch
        {
            _cpuCounter = null;
        }

        // GPU – WDDM 2.x performance counter (Windows 10 1703+, requires a
        // WDDM 2.0 driver). Available as Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine.
        try
        {
            _gpuSearcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage " +
                "FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
        }
        catch
        {
            _gpuSearcher = null;
        }

        // Thermal zones (root\wmi namespace, may require elevation on some OEMs).
        try
        {
            _tempSearcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        }
        catch
        {
            _tempSearcher = null;
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Polls all metrics on a thread-pool thread and returns a snapshot.
    /// Safe to await from the UI thread.
    /// </summary>
    public Task<SystemMetrics> GetMetricsAsync() =>
        Task.Run(CollectMetrics);

    // ── Internal helpers ─────────────────────────────────────────────────────
    private SystemMetrics CollectMetrics()
    {
        var m = new SystemMetrics();

        ReadCpu(m);
        ReadMemory(m);
        ReadGpu(m);
        ReadTemperature(m);

        return m;
    }

    private void ReadCpu(SystemMetrics m)
    {
        try
        {
            if (_cpuCounter != null)
                m.CpuUsage = Math.Clamp(_cpuCounter.NextValue(), 0f, 100f);
        }
        catch { /* counter may be unavailable in some sandboxed environments */ }
    }

    private static void ReadMemory(SystemMetrics m)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            return;

        const float gib = 1024f * 1024f * 1024f;
        m.MemoryTotalGb = status.ullTotalPhys / gib;
        m.MemoryUsedGb  = (status.ullTotalPhys - status.ullAvailPhys) / gib;
        m.MemoryUsage   = m.MemoryUsedGb / m.MemoryTotalGb * 100f;
    }

    private void ReadGpu(SystemMetrics m)
    {
        if (_gpuSearcher == null)
            return;
        try
        {
            float maxUtil = 0f;
            using var results = _gpuSearcher.Get();
            foreach (ManagementBaseObject baseObj in results)
            {
                using var obj = (ManagementObject)baseObj;
                // Filter to 3-D engine entries (the most representative for GPU load)
                var name = obj["Name"]?.ToString() ?? string.Empty;
                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    continue;

                var util = Convert.ToSingle(obj["UtilizationPercentage"]);
                if (util > maxUtil)
                    maxUtil = util;
            }
            m.GpuUsage = Math.Clamp(maxUtil, 0f, 100f);
        }
        catch { /* GPU counters may disappear after a driver reset */ }
    }

    private void ReadTemperature(SystemMetrics m)
    {
        if (_tempSearcher == null)
            return;
        try
        {
            float maxCelsius = 0f;
            using var results = _tempSearcher.Get();
            foreach (ManagementBaseObject baseObj in results)
            {
                using var obj = (ManagementObject)baseObj;
                // MSAcpi_ThermalZoneTemperature reports in tenths of Kelvin
                double tenthsKelvin = Convert.ToDouble(obj["CurrentTemperature"]);
                float celsius = (float)((tenthsKelvin - KelvinOffsetTenths) / 10.0);
                if (celsius > maxCelsius)
                    maxCelsius = celsius;
            }
            m.Temperature = maxCelsius > 0f ? maxCelsius : 0f;
        }
        catch { /* thermal WMI may require elevated privileges */ }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cpuCounter?.Dispose();
        _gpuSearcher?.Dispose();
        _tempSearcher?.Dispose();
    }
}
