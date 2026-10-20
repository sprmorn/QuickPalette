namespace QuickPalette.Application.Events;

using Serilog;

/// <summary>
/// 进程内轻量事件总线（发布/订阅）。
/// 线程安全；订阅返回 IDisposable 句柄（Dispose 后保证不再被调用）；
/// handler 异常不再静默吞：记日志并可经 errorHandler 回调，且不影响其他订阅者。
/// </summary>
public class EventBus : IDisposable
{
    private readonly ILogger _logger;
    private readonly Action<Exception, object?>? _errorHandler;
    private readonly Dictionary<Type, List<Subscription>> _handlers = new();
    private readonly object _lock = new();

    public EventBus(ILogger logger) : this(logger, null)
    {
    }

    /// <param name="logger">日志（可为 null，内部以 Silent 兜底）</param>
    /// <param name="errorHandler">handler 异常回调（异常, 事件对象）</param>
    public EventBus(ILogger? logger, Action<Exception, object?>? errorHandler)
    {
        _logger = logger ?? new LoggerConfiguration().CreateLogger();
        _errorHandler = errorHandler;
    }

    private sealed class Subscription
    {
        public Subscription(Delegate handler) => Handler = handler;
        public Delegate Handler { get; }
        public bool Active = true;
    }

    /// <summary>
    /// 订阅事件（返回句柄：Dispose 即退订；退订后保证不再被调用）
    /// </summary>
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(typeof(TEvent), handler);
    }

    /// <summary>
    /// 订阅异步 handler（Publish 不阻塞其完成；PublishAsync 等待全部完成）
    /// </summary>
    public IDisposable SubscribeAsync<TEvent>(Func<TEvent, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(typeof(TEvent), handler);
    }

    private IDisposable Add(Type eventType, Delegate handler)
    {
        var sub = new Subscription(handler);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(eventType, out var list))
            {
                list = new List<Subscription>();
                _handlers[eventType] = list;
            }
            list.Add(sub);
        }
        _logger.Debug("Subscribed to {EventType}", eventType.Name);
        return new SubscriptionHandle(this, eventType, sub);
    }

    /// <summary>
    /// 发布事件（同步）：异步 handler 以"即发即弃"方式触发（异常进日志），不阻塞发布者。
    /// </summary>
    public void Publish<TEvent>(TEvent @event)
    {
        if (@event is null) return;
        foreach (var sub in Snapshot(typeof(TEvent)))
        {
            if (!sub.Active) continue; // 退订后不再被调用
            try
            {
                switch (sub.Handler)
                {
                    case Action<TEvent> sync:
                        sync(@event);
                        break;
                    case Func<TEvent, Task> async:
                        _ = Observe(async(@event), @event);
                        break;
                }
            }
            catch (Exception ex)
            {
                ReportError(ex, @event);
            }
        }
    }

    /// <summary>
    /// 发布事件（异步）：等待全部异步 handler 完成；同步 handler 仍同步执行。
    /// </summary>
    public Task PublishAsync<TEvent>(TEvent @event)
    {
        if (@event is null) return Task.CompletedTask;
        var pending = new List<Task>();
        foreach (var sub in Snapshot(typeof(TEvent)))
        {
            if (!sub.Active) continue;
            try
            {
                switch (sub.Handler)
                {
                    case Action<TEvent> sync:
                        sync(@event);
                        break;
                    case Func<TEvent, Task> async:
                        pending.Add(async(@event));
                        break;
                }
            }
            catch (Exception ex)
            {
                ReportError(ex, @event);
            }
        }
        return pending.Count == 0 ? Task.CompletedTask : Observe(Task.WhenAll(pending), @event);
    }

    /// <summary>
    /// 取消订阅（按 handler 精确匹配；与句柄 Dispose 等效）
    /// </summary>
    public void Unsubscribe<TEvent>(Action<TEvent> handler)
    {
        if (handler is null) return;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list)) return;
            foreach (var sub in list)
            {
                // 同一方法组每次转换产生不同委托实例：引用比较 + 方法/目标等价比较
                if (ReferenceEquals(sub.Handler, handler) || sub.Handler.Equals(handler))
                    sub.Active = false;
            }
            list.RemoveAll(s => !s.Active);
        }
    }

    private List<Subscription> Snapshot(Type eventType)
    {
        lock (_lock)
        {
            return _handlers.TryGetValue(eventType, out var list)
                ? new List<Subscription>(list)
                : new List<Subscription>();
        }
    }

    private async Task Observe(Task task, object? evt)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportError(ex, evt);
        }
    }

    private void ReportError(Exception ex, object? evt)
    {
        _logger.Error(ex, "Event handler error for {EventType}", evt?.GetType().Name ?? "unknown");
        if (_errorHandler is null) return;
        try
        {
            _errorHandler(ex, evt);
        }
        catch (Exception cbEx)
        {
            _logger.Error(cbEx, "EventBus error handler threw");
        }
    }

    /// <summary>句柄 Dispose 即退订</summary>
    private sealed class SubscriptionHandle : IDisposable
    {
        private EventBus? _bus;
        private readonly Type _eventType;
        private readonly Subscription _sub;

        public SubscriptionHandle(EventBus bus, Type eventType, Subscription sub)
        {
            _bus = bus;
            _eventType = eventType;
            _sub = sub;
        }

        public void Dispose()
        {
            var bus = Interlocked.Exchange(ref _bus, null);
            if (bus is null) return;
            _sub.Active = false;
            lock (bus._lock)
            {
                if (bus._handlers.TryGetValue(_eventType, out var list))
                    list.Remove(_sub);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var list in _handlers.Values)
                foreach (var s in list) s.Active = false;
            _handlers.Clear();
        }
    }
}

#region 事件定义

/// <summary>
/// 应用加入白名单
/// </summary>
public record AppAddedEvent(string AppId, string AppName);

/// <summary>
/// 应用启动成功/失败
/// </summary>
public record AppLaunchedEvent(string AppId, bool Success, string? Error);

/// <summary>
/// 热键触发
/// </summary>
public record HotkeyPressedEvent;

/// <summary>
/// 配置变更（带新旧值，供各模块增量响应）
/// </summary>
public record ConfigChangedEvent(string Key, object? OldValue = null, object? NewValue = null);

/// <summary>
/// 候选池更新
/// </summary>
public record CandidatesUpdatedEvent;

#endregion
