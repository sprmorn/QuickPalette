namespace QuickPalette.Infrastructure.Singleton;

using Serilog;

/// <summary>
/// 单实例保护 - 防止双开覆盖数据。
/// 锁名：Local\&lt;appName&gt;-&lt;用户名&gt;（每用户一份实例，不跨用户互斥）。
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly string _mutexName;
    private readonly ILogger _logger;

    private Mutex? _mutex;
    private bool _hasLock;
    private bool _disposed;

    public SingleInstanceLock(string appName, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(appName);
        _logger = logger;

        // 数据每用户一份：Local\<appName>-<用户名>
        _mutexName = $@"Local\{appName}-{Environment.UserName}";

        try
        {
            _mutex = new Mutex(true, _mutexName, out var createdNew);
            _hasLock = createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // 权限受限场景（如其他用户/提升进程创建的同名互斥体）：按"已在运行"处理
            _mutex = null;
            _hasLock = false;
            _logger.Warning("Mutex access denied for {MutexName}, treat as another instance running", _mutexName);
        }
    }

    /// <summary>
    /// 尝试获取锁；已在运行返回 false
    /// </summary>
    public bool TryAcquire()
    {
        if (_disposed)
            return _hasLock;

        if (_hasLock)
            return true;

        if (_mutex == null)
        {
            _logger.Warning("Another instance is already running (mutex inaccessible: {MutexName})", _mutexName);
            return false;
        }

        try
        {
            _hasLock = _mutex.WaitOne(0, false);
            if (_hasLock)
            {
                _logger.Information("Single instance lock acquired: {MutexName}", _mutexName);
            }
            else
            {
                _logger.Warning("Another instance is already running: {MutexName}", _mutexName);
            }
        }
        catch (AbandonedMutexException)
        {
            // 前一个实例崩溃/退出未释放：锁归当前线程所有，视为获锁成功
            _hasLock = true;
            _logger.Warning("Acquired abandoned mutex (previous instance crashed): {MutexName}", _mutexName);
        }
        catch (UnauthorizedAccessException)
        {
            // 权限受限：按"已在运行"处理
            _hasLock = false;
            _logger.Warning("Mutex access denied on acquire: {MutexName}, treat as already running", _mutexName);
        }

        return _hasLock;
    }

    /// <summary>
    /// 是否已获取锁
    /// </summary>
    public bool HasLock => _hasLock;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_mutex == null)
            return;

        if (_hasLock)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                // 非持有线程调用 ReleaseMutex 等异常不影响最终释放
                _logger.Warning(ex, "Release mutex failed: {MutexName}", _mutexName);
            }

            _hasLock = false;
        }

        // 完整释放内核句柄；即使进程退出/崩溃，操作系统也会回收互斥体，不留死锁
        _mutex.Dispose();
        _mutex = null;
    }
}
