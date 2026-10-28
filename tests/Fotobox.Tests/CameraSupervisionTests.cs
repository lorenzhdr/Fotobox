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

public sealed class FrameWatchdogFirstFrameTests
{
    private static readonly TimeSpan First = TimeSpan.FromSeconds(8);

    private static (FrameWatchdog Dog, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        return (new FrameWatchdog(time, First, TimeSpan.FromSeconds(5)), time);
    }

    [Fact]
    public async Task WaitForFirstFrame_ReturnsTrue_WhenFrameArrivesInTime()
    {
        var (dog, time) = Create();
        var wait = dog.WaitForFirstFrameAsync();
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.False(wait.IsCompleted);

        dog.NotifyFrame();
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitForFirstFrame_ReturnsFalse_AfterFirstFrameTimeout()
    {
        var (dog, time) = Create();
        var wait = dog.WaitForFirstFrameAsync();
        time.Advance(First - TimeSpan.FromMilliseconds(1));
        Assert.False(wait.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(dog.HasFrame);
    }

    [Fact]
    public async Task WaitForFirstFrame_ReturnsTrue_WhenFrameAlreadyThere()
    {
        var (dog, _) = Create();
        dog.NotifyFrame();
        Assert.True(await dog.WaitForFirstFrameAsync());
    }

    [Fact]
    public async Task WaitForFirstFrame_Cancels()
    {
        var (dog, _) = Create();
        using var cts = new CancellationTokenSource();
        var wait = dog.WaitForFirstFrameAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RepeatedFrames_DoNotThrow()
    {
        var (dog, _) = Create();
        for (var i = 0; i < 10; i++)
            dog.NotifyFrame();
        Assert.True(dog.HasFrame);
    }
}

public sealed class CameraReconnectBackoffTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(10, 15)]
    public void Interval_GrowsAfterRepeatedNoImageAttempts(int attempts, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CameraReconnectBackoff.GetInterval(attempts));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(-1, 0)]
    [InlineData(-3, 10)]
    public void ShouldAttempt_WithoutHangingOperation_IsAlwaysTrue(int hanging, int sinceSeconds) =>
        Assert.True(CameraReconnectBackoff.ShouldAttempt(hanging, TimeSpan.FromSeconds(sinceSeconds)));

    [Theory]
    [InlineData(1, 10)]
    [InlineData(1, 0)]
    [InlineData(3, 299)]
    public void ShouldAttempt_WithHangingOperation_WaitsForRetryInterval(int hanging, int sinceSeconds) =>
        Assert.False(CameraReconnectBackoff.ShouldAttempt(hanging, TimeSpan.FromSeconds(sinceSeconds)));

    [Theory]
    [InlineData(1, 300)]
    [InlineData(3, 301)]
    [InlineData(2, 3600)]
    public void ShouldAttempt_WithHangingOperation_TrueAfterRetryInterval(int hanging, int sinceSeconds) =>
        Assert.True(CameraReconnectBackoff.ShouldAttempt(hanging, TimeSpan.FromSeconds(sinceSeconds)));

    [Fact]
    public void ShouldAttempt_NegativeElapsedWithHangingOperation_IsFalse() =>
        Assert.False(CameraReconnectBackoff.ShouldAttempt(1, TimeSpan.FromSeconds(-5)));
}

public sealed class FrameErrorThrottleTests
{
    [Fact]
    public void FirstErrorOfASeries_IsLogged_FollowingOnesOnlyEveryNth()
    {
        var throttle = new FrameErrorThrottle();
        Assert.True(throttle.OnError(out var first));
        Assert.Equal(1, first);

        var logged = 0;
        for (var i = 2; i <= FrameErrorThrottle.RepeatEvery * 2; i++)
        {
            if (throttle.OnError(out var count))
            {
                logged++;
                Assert.Equal(0, count % FrameErrorThrottle.RepeatEvery);
            }
        }
        Assert.Equal(2, logged);
    }

    [Fact]
    public void Success_EndsTheSeries_AndReportsItsLength()
    {
        var throttle = new FrameErrorThrottle();
        Assert.Equal(0, throttle.OnSuccess());

        throttle.OnError(out _);
        throttle.OnError(out _);
        throttle.OnError(out _);
        Assert.Equal(3, throttle.OnSuccess());
        Assert.Equal(0, throttle.OnSuccess());

        // Neue Serie: wieder wird der erste Fehler gemeldet.
        Assert.True(throttle.OnError(out var count));
        Assert.Equal(1, count);
    }
}

public sealed class CameraTimeoutExceptionTests
{
    [Fact]
    public void Pending_CarriesTheRunningOperation_AndIsOptional()
    {
        var pending = new TaskCompletionSource().Task;
        Assert.Same(pending, new CameraTimeoutException("Start", pending).Pending);
        Assert.Null(new CameraTimeoutException("Initialisierung").Pending);
    }
}
