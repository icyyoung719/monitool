namespace MonitorTool.Models;

/// <summary>
/// Snapshot of current system performance metrics.
/// </summary>
public sealed class SystemMetrics
{
    /// <summary>Overall CPU utilisation, 0–100 %.</summary>
    public float CpuUsage { get; set; }

    /// <summary>RAM utilisation, 0–100 %.</summary>
    public float MemoryUsage { get; set; }

    /// <summary>RAM in use, GiB.</summary>
    public float MemoryUsedGb { get; set; }

    /// <summary>Total installed RAM, GiB.</summary>
    public float MemoryTotalGb { get; set; }

    /// <summary>3-D engine GPU utilisation, 0–100 %. 0 if unavailable.</summary>
    public float GpuUsage { get; set; }

    /// <summary>Highest reported thermal zone temperature, °C. 0 if unavailable.</summary>
    public float Temperature { get; set; }
}
