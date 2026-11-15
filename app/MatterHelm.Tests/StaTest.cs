using System.Diagnostics;
using System.Runtime.ExceptionServices;
using MatterHelm.Ui;

namespace MatterHelm.Tests;

/// <summary>Runs WinForms assertions on a dedicated STA thread with a live message pump.</summary>
/// <remarks>
/// On x64 Windows, an exception thrown while USER32 synchronously calls back into
/// a handler such as <see cref="Control.OnResize"/> during SetBounds/Size can be
/// swallowed below the managed message pump. The test body may then never regain
/// control and this harness reports its documented timeout instead of the original
/// exception; do not use it as propagation proof for that kernel-callback path.
/// </remarks>
internal static class StaTest
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    internal static void Run(Action body, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ExceptionDispatchInfo? captured = null;
        TimeSpan wait = timeout ?? DefaultTimeout;
        HeadlessDesktop.RunOnStaThread(() =>
        {
            // HeadlessDesktop bound this native thread before OleInitialize;
            // no WinForms parking window or hook can precede that boundary.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
            var synchronizationContext = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            using var context = new ApplicationContext();
            bool synchronizationContextDisposed = false;
            void ExitThread()
            {
                SynchronizationContext.SetSynchronizationContext(null);
                synchronizationContext.Dispose();
                synchronizationContextDisposed = true;
                context.ExitThread();
            }

            EventHandler? runOnce = null;
            runOnce = (_, _) =>
            {
                Application.Idle -= runOnce;
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    captured = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    ExitThread();
                }
            };

            Application.Idle += runOnce;
            try
            {
                Application.Run(context);
            }
            catch (Exception ex)
            {
                captured ??= ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Application.Idle -= runOnce;
                if (!synchronizationContextDisposed)
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    synchronizationContext.Dispose();
                }
            }
        }, wait);

        captured?.Throw();
    }

    internal static void Run(Func<Task> body, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ExceptionDispatchInfo? captured = null;
        TimeSpan wait = timeout ?? DefaultTimeout;
        HeadlessDesktop.RunOnStaThread(() =>
        {
            // HeadlessDesktop bound this native thread before OleInitialize;
            // no WinForms parking window or hook can precede that boundary.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
            var synchronizationContext = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            using var context = new ApplicationContext();
            bool synchronizationContextDisposed = false;
            void ExitThread()
            {
                SynchronizationContext.SetSynchronizationContext(null);
                synchronizationContext.Dispose();
                synchronizationContextDisposed = true;
                context.ExitThread();
            }

            EventHandler? runOnce = null;
            runOnce = (_, _) =>
            {
                Application.Idle -= runOnce;
                try
                {
                    Task task = body();
                    TaskScheduler staScheduler = TaskScheduler.FromCurrentSynchronizationContext();
                    _ = task.ContinueWith(
                        completedTask =>
                        {
                            try
                            {
                                completedTask.GetAwaiter().GetResult();
                            }
                            catch (Exception ex)
                            {
                                captured = ExceptionDispatchInfo.Capture(ex);
                            }
                            finally
                            {
                                ExitThread();
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        staScheduler);
                }
                catch (Exception ex)
                {
                    captured = ExceptionDispatchInfo.Capture(ex);
                    ExitThread();
                }
            };

            Application.Idle += runOnce;
            try
            {
                Application.Run(context);
            }
            catch (Exception ex)
            {
                captured ??= ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Application.Idle -= runOnce;
                if (!synchronizationContextDisposed)
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    synchronizationContext.Dispose();
                }
            }
        }, wait);

        captured?.Throw();
    }

    internal static void PumpUntilIdle(Control control, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        bool completedTwoTurns = false;
        // A second posted callback also drains work queued by the first turn
        // (notably Shown and its follow-up AutoSize layout).
        control.BeginInvoke((Action)(() =>
        {
            control.BeginInvoke((Action)(() => completedTwoTurns = true));
        }));

        TimeSpan wait = timeout ?? DefaultTimeout;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!completedTwoTurns)
        {
            Application.DoEvents();
            if (stopwatch.Elapsed >= wait)
            {
                throw new TimeoutException($"WinForms did not settle within {wait.TotalSeconds:0.###} seconds.");
            }
        }

        Application.DoEvents();
    }
}
