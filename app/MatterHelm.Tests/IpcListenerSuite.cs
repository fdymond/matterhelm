using Xunit;

namespace MatterHelm.Tests;

/// <summary>
/// Shared collection for every test that binds a real <see cref="System.Net.HttpListener"/>.
///
/// <para>xUnit runs distinct collections in parallel, so the BridgeHost wiring
/// suite and <c>IpcServerTests</c> used to stand up listeners simultaneously.
/// That held until the wiring suite grew in 0.7.0, when the GitHub runner began
/// failing with <c>ArgumentException: The handle is invalid</c> out of
/// <c>HttpListener.BeginGetContext</c> — HTTP.sys pressure, not a product
/// defect: the same tests pass locally and passed in CI one release earlier.
/// Sharing one collection with parallelisation disabled keeps at most one
/// listener alive at a time; the tests are integration-shaped and fast, so the
/// serial cost is negligible.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IpcListenerSuite
{
    /// <summary>Collection name applied to listener-bound test classes.</summary>
    public const string Name = "ipc-listeners";
}
