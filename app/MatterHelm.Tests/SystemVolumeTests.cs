using MatterHelm.Actions;
using Xunit;

namespace MatterHelm.Tests;

public sealed class SystemVolumeTests
{
    [Fact]
    public void TenDefaultDeviceNotificationsQueueOneReacquirePass()
    {
        var queued = new List<Action>();
        int reacquires = 0;
        var scheduler = new EndpointReacquireScheduler(
            () => reacquires++,
            queued.Add);

        for (int i = 0; i < 10; i++)
        {
            scheduler.Schedule();
        }

        Action work = Assert.Single(queued);
        work();
        Assert.Equal(1, reacquires);
    }

    [Fact]
    public void NotificationDuringReacquireRunsASecondPass()
    {
        var queued = new List<Action>();
        int reacquires = 0;
        EndpointReacquireScheduler? scheduler = null;
        scheduler = new EndpointReacquireScheduler(
            () =>
            {
                reacquires++;
                if (reacquires == 1)
                {
                    scheduler!.Schedule();
                }
            },
            queued.Add);

        scheduler.Schedule();
        Assert.Single(queued)();

        Assert.Equal(2, reacquires);
        Assert.Single(queued);
    }

    [Fact]
    public void QueueFailureReturnsSchedulerToIdle()
    {
        int queueAttempts = 0;
        var queued = new List<Action>();
        var scheduler = new EndpointReacquireScheduler(
            () => { },
            work =>
            {
                queueAttempts++;
                if (queueAttempts == 1)
                {
                    throw new InvalidOperationException("queue unavailable");
                }

                queued.Add(work);
            });

        Assert.Throws<InvalidOperationException>(scheduler.Schedule);
        scheduler.Schedule();

        Assert.Single(queued);
    }

    [Fact]
    public void ReacquireFailureReturnsSchedulerToIdleForALaterSuccessfulSchedule()
    {
        var queued = new List<Action>();
        int reacquires = 0;
        var scheduler = new EndpointReacquireScheduler(
            () =>
            {
                if (++reacquires == 1)
                {
                    throw new InvalidOperationException("endpoint unavailable");
                }
            },
            queued.Add);

        scheduler.Schedule();
        Assert.Throws<InvalidOperationException>(queued[0]);
        scheduler.Schedule();
        queued[1]();

        Assert.Equal(2, reacquires);
        Assert.Equal(2, queued.Count);
    }
}
