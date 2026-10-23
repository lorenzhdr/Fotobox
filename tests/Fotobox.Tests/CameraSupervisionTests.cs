using Microsoft.Extensions.Time.Testing;
using Xunit;
using Fotobox.Services.Camera;

namespace Fotobox.Tests;

public sealed class FrameWatchdogTests
{
    private static readonly TimeSpan First = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(5);

    private static (FrameWatchdog Dog, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        return (new FrameWatchdog(time, First, Stall), time);
    }

    [Fact]
    public void DefaultTimeouts_AreAroundFiveToEightSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), FrameWatchdog.FirstFrameTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), FrameWatchdog.StallTimeout);
    }

    [Fact]
    public void NoFrameYet_IsNotStalledBeforeFirstFrameTimeout()
    {
        var (dog, time) = Create();
        time.Advance(First - TimeSpan.FromMilliseconds(1));
        Assert.False(dog.IsStalled);
        Assert.False(dog.HasFrame);
    }

    [Fact]
    public void NoFrameYet_IsStalledAfterFirstFrameTimeout()
    {
        var (dog, time) = Create();
        time.Advance(First);
        Assert.True(dog.IsStalled);
    }

    [Fact]
    public void FirstFrame_SwitchesToStallTimeout()
    {
        var (dog, time) = Create();
        time.Advance(TimeSpan.FromSeconds(2));
        dog.NotifyFrame();
        Assert.True(dog.HasFrame);

        time.Advance(Stall - TimeSpan.FromMilliseconds(1));
        Assert.False(dog.IsStalled);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(dog.IsStalled);
    }

    [Fact]
    public void RegularFrames_KeepWatchdogQuiet()
    {
        var (dog, time) = Create();
        for (var i = 0; i < 100; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            dog.NotifyFrame();
            Assert.False(dog.IsStalled);
        }
    }

    [Fact]
    public void FramesStopping_TriggersAfterStallTimeout()
    {
        var (dog, time) = Create();
        dog.NotifyFrame();
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(dog.IsStalled);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(dog.IsStalled);
    }

    [Fact]
    public void TimeUntilStall_ShrinksWithTime()
    {
        var (dog, time) = Create();
        Assert.Equal(First, dog.TimeUntilStall);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(5), dog.TimeUntilStall);
    }

    [Fact]
    public void Arm_RestartsFirstFrameWindow_UntilFirstFrame()
    {
        var (dog, time) = Create();
        time.Advance(TimeSpan.FromSeconds(6));
        dog.Arm();
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(dog.IsStalled);

        dog.NotifyFrame();
        time.Advance(TimeSpan.FromSeconds(3));
        dog.Arm(); // nach dem ersten Bild wirkungslos
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(dog.IsStalled);
    }
}

public sealed class CameraStartRetryTests
{
    [Fact]
    public void Delays_AreBetween500And1000Milliseconds_AndAtMostThree()
    {
        Assert.InRange(CameraStartRetry.Delays.Length, 1, 3);
        Assert.All(CameraStartRetry.Delays, d => Assert.InRange(d.TotalMilliseconds, 500, 1000));
    }

    [Fact]
    public void Retries_AreOfferedInOrder_ThenExhausted()
    {
        for (var i = 0; i < CameraStartRetry.Delays.Length; i++)
        {
            Assert.True(CameraStartRetry.TryGetDelay(i, TimeSpan.FromSeconds(1), out var delay));
            Assert.Equal(CameraStartRetry.Delays[i], delay);
        }

        Assert.False(CameraStartRetry.TryGetDelay(CameraStartRetry.Delays.Length, TimeSpan.Zero, out var none));
        Assert.Equal(TimeSpan.Zero, none);
    }

    [Fact]
    public void NoRetry_WhenBudgetWouldBeExceeded()
    {
        var nearlyOut = CameraStartRetry.Budget - TimeSpan.FromMilliseconds(100);
        Assert.False(CameraStartRetry.TryGetDelay(0, nearlyOut, out _));
        Assert.False(CameraStartRetry.TryGetDelay(0, CameraStartRetry.Budget, out _));
    }

    [Fact]
    public void NegativeRetryCount_IsRejected() =>
        Assert.False(CameraStartRetry.TryGetDelay(-1, TimeSpan.Zero, out _));

    [Fact]
    public void TotalDelays_StayWithinBudget() =>
        Assert.True(CameraStartRetry.Delays.Aggregate(TimeSpan.Zero, (a, d) => a + d) < CameraStartRetry.Budget);
}

public sealed class CameraMessagesTests
{
    [Fact]
    public void NewMessages_AreGuestFriendlyGerman()
    {
        Assert.Contains("Kamera", CameraMessages.NoImage);
        Assert.Contains("prüfe", CameraMessages.NoImage);
        Assert.Contains("reagiert nicht", CameraMessages.NotResponding);
    }

    [Fact]
    public void CameraTimeoutException_IsATimeoutException() =>
        Assert.IsAssignableFrom<TimeoutException>(new CameraTimeoutException("Start"));
}
