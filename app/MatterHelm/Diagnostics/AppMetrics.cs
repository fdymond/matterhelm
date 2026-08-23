using System.Diagnostics.Metrics;

namespace MatterHelm.Diagnostics;

/// <summary>
/// The app's in-box <see cref="Meter"/> and its instruments (ADR-006 §2).
/// Increments live where the events already are: <c>BridgeHost</c> for
/// actions/acks/restarts/state frames, <c>IpcServer</c> for authenticated
/// connect/disconnect (so a bridge-disable teardown still counts its
/// disconnect); the values are consumed
/// in-process by <see cref="MetricsFileListener"/> and externally via
/// <c>dotnet-counters monitor -n MatterHelm --counters MatterHelm</c>.
/// Local-only capture — deliberately no OpenTelemetry SDK (ADR-006 §2).
/// </summary>
public static class AppMetrics
{
    /// <summary>The meter name, for listeners and <c>dotnet-counters</c>.</summary>
    public const string MeterName = "MatterHelm";

    private static readonly Meter _meter = new(MeterName);

    /// <summary>Action frames that executed successfully (ok ack sent back).</summary>
    public static Counter<long> ActionsExecutedOk { get; } =
        _meter.CreateCounter<long>("actions_executed_ok", description: "Action frames executed successfully.");

    /// <summary>Action frames whose execution failed (fail ack sent back).</summary>
    public static Counter<long> ActionsFailed { get; } =
        _meter.CreateCounter<long>("actions_failed", description: "Action frames whose execution failed.");

    /// <summary>Acks (ok or fail) actually delivered to the sidecar.</summary>
    public static Counter<long> AcksSent { get; } =
        _meter.CreateCounter<long>("acks_sent", description: "Acks delivered to the sidecar.");

    /// <summary>Sidecar restarts scheduled by the supervisor after unexpected exits.</summary>
    public static Counter<long> SupervisorRestarts { get; } =
        _meter.CreateCounter<long>("supervisor_restarts", description: "Sidecar restarts scheduled by the supervisor.");

    /// <summary>Sidecar IPC connections that completed the token handshake.</summary>
    public static Counter<long> IpcClientConnects { get; } =
        _meter.CreateCounter<long>("ipc_client_connects", description: "Authenticated sidecar IPC connections.");

    /// <summary>Authenticated sidecar IPC connections that ended.</summary>
    public static Counter<long> IpcClientDisconnects { get; } =
        _meter.CreateCounter<long>("ipc_client_disconnects", description: "Authenticated sidecar IPC disconnects.");

    /// <summary>Volume/mute state frames delivered to the sidecar.</summary>
    public static Counter<long> StateFramesPublished { get; } =
        _meter.CreateCounter<long>("state_frames_published", description: "State frames delivered to the sidecar.");

    /// <summary>State publishes swallowed by the volume echo dead-band.</summary>
    public static Counter<long> StateFramesSuppressed { get; } =
        _meter.CreateCounter<long>("state_frames_suppressed", description: "State publishes suppressed by the echo dead-band.");

    /// <summary>Per-action execution latency (frame received → executed), in milliseconds.</summary>
    public static Histogram<double> ActionExecuteMs { get; } =
        _meter.CreateHistogram<double>("action_execute_ms", "ms", "Action execution latency.");

    // S9-6 resource gauges: observed on demand (each snapshot flush /
    // dotnet-counters poll), so memory truth lands in metrics-*.jsonl and a
    // leak shows up as a trend line instead of needing Task Manager. The
    // ADR-007 budgets these watch: tray private bytes ≤ 32 MB.

    /// <summary>Process private bytes (the ADR-007 budget metric).</summary>
    public static ObservableGauge<long> ProcessPrivateBytes { get; } =
        _meter.CreateObservableGauge(
            "process_private_bytes",
            static () =>
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                return process.PrivateMemorySize64;
            },
            "bytes",
            "Process private bytes (ADR-007 tray budget: 32 MB).");

    /// <summary>Managed (GC) heap bytes — the .NET share of private bytes.</summary>
    public static ObservableGauge<long> GcHeapBytes { get; } =
        _meter.CreateObservableGauge(
            "gc_heap_bytes",
            static () => GC.GetTotalMemory(forceFullCollection: false),
            "bytes",
            "Managed heap size (no forced collection).");

    /// <summary>OS handle count — the churn-probe leak signal, continuously.</summary>
    public static ObservableGauge<long> ProcessHandleCount { get; } =
        _meter.CreateObservableGauge(
            "process_handle_count",
            static () =>
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                return (long)process.HandleCount;
            },
            description: "OS handle count.");

    /// <summary>Thread count — a runaway background-work signal.</summary>
    public static ObservableGauge<long> ProcessThreadCount { get; } =
        _meter.CreateObservableGauge(
            "process_thread_count",
            static () =>
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                return (long)process.Threads.Count;
            },
            description: "OS thread count.");
}
