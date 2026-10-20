namespace QuickPalette.Domain.Tests;

using QuickPalette.Application.Events;
using Serilog;
using Xunit;

/// <summary>
/// EventBus 订阅/退订/异常隔离测试
/// </summary>
public class EventBusTests
{
    private static EventBus Bus(Action<Exception, object?>? onError = null)
        => new(new LoggerConfiguration().CreateLogger(), onError);

    [Fact]
    public void Subscriber_Receives_Publish()
    {
        var bus = Bus();
        string? got = null;
        bus.Subscribe<AppAddedEvent>(e => got = e.AppName);

        bus.Publish(new AppAddedEvent("id1", "微信"));

        Assert.Equal("微信", got);
    }

    [Fact]
    public void HandleDispose_Stops_Delivery()
    {
        var bus = Bus();
        var count = 0;
        var handle = bus.Subscribe<AppAddedEvent>(_ => count++);

        bus.Publish(new AppAddedEvent("id", "a"));
        handle.Dispose();
        bus.Publish(new AppAddedEvent("id", "b"));

        Assert.Equal(1, count); // 退订后不再被调用
    }

    [Fact]
    public void Unsubscribe_ByHandler_Works()
    {
        var bus = Bus();
        var count = 0;
        void Handler(AppAddedEvent _) => count++;
        bus.Subscribe<AppAddedEvent>(Handler);

        bus.Unsubscribe<AppAddedEvent>(Handler);
        bus.Publish(new AppAddedEvent("id", "a"));

        Assert.Equal(0, count);
    }

    [Fact]
    public void HandlerException_Isolated_And_Reported()
    {
        Exception? reported = null;
        var bus = Bus((ex, _) => reported = ex);
        var secondCalled = false;

        bus.Subscribe<AppAddedEvent>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<AppAddedEvent>(_ => secondCalled = true);

        bus.Publish(new AppAddedEvent("id", "a"));

        Assert.True(secondCalled);          // 其他订阅者不受影响
        Assert.IsType<InvalidOperationException>(reported); // 异常不再静默吞
    }

    [Fact]
    public async Task PublishAsync_Waits_AsyncHandlers()
    {
        var bus = Bus();
        var completed = false;
        bus.SubscribeAsync<AppAddedEvent>(async _ =>
        {
            await Task.Delay(20);
            completed = true;
        });

        await bus.PublishAsync(new AppAddedEvent("id", "a"));

        Assert.True(completed);
    }

    [Fact]
    public void Publish_Null_DoesNotThrow()
    {
        var bus = Bus();
        bus.Publish<AppAddedEvent>(null!);
    }

    [Fact]
    public void ConfigChangedEvent_Carries_OldAndNewValues()
    {
        var bus = Bus();
        ConfigChangedEvent? got = null;
        bus.Subscribe<ConfigChangedEvent>(e => got = e);

        bus.Publish(new ConfigChangedEvent("Hotkey", "Alt+S", "Ctrl+Alt+Space"));

        Assert.Equal("Alt+S", got!.OldValue);
        Assert.Equal("Ctrl+Alt+Space", got.NewValue);
    }
}
