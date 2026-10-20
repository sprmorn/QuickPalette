namespace QuickPalette.Infrastructure.Tests;

// xUnit1031：会话期 TestRunner 以反射同步执行测试（不 await 异步返回），
// 测试方法内必须同步阻塞等待 Task，确保断言真正生效。
#pragma warning disable xUnit1031

using QuickPalette.Infrastructure.Singleton;
using Serilog;

/// <summary>
/// SingleInstanceLock 测试：同名二次获取返回"已在运行"、释放后可再获取、崩溃遗留锁视为获锁。
/// 互斥体按线程持有/释放，因此跨实例获取都在独立线程上进行。
/// </summary>
public class SingleInstanceLockTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static string NewAppName() => "QPTest-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void TryAcquire_FirstInstance_Succeeds()
    {
        using var first = new SingleInstanceLock(NewAppName(), Logger);

        Assert.True(first.TryAcquire());
        Assert.True(first.HasLock);

        // 同一实例重复获取仍为 true
        Assert.True(first.TryAcquire());
    }

    [Fact]
    public void SecondAcquire_SameName_ReturnsAlreadyRunning()
    {
        var name = NewAppName();
        using var first = new SingleInstanceLock(name, Logger);
        Assert.True(first.TryAcquire());

        // 另一线程（模拟另一实例）获取同名锁 → "已在运行"。
        // 必须用真正的独立线程：Task.Run 的工作项可能被内联到当前线程执行，
        // 而互斥体对持有线程递归可重入（同线程 WaitOne(0) 成功），断言会随线程归属性翻转。
        var secondHeld = false;
        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var second = new SingleInstanceLock(name, Logger);
                secondHeld = second.TryAcquire();
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.Null(threadError);
        Assert.False(secondHeld);
    }

    [Fact]
    public void DifferentNames_BothCanAcquire()
    {
        using var a = new SingleInstanceLock(NewAppName(), Logger);
        using var b = new SingleInstanceLock(NewAppName(), Logger);

        Assert.True(a.TryAcquire());
        Assert.True(b.TryAcquire());
    }

    [Fact]
    public void AfterDispose_NewInstanceCanAcquire()
    {
        var name = NewAppName();
        var first = new SingleInstanceLock(name, Logger);
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var second = new SingleInstanceLock(name, Logger);
        Assert.True(second.TryAcquire());
        Assert.True(second.HasLock);
    }

    [Fact]
    public void AbandonedMutex_TreatedAsAcquired()
    {
        var name = NewAppName();

        // 后台线程持锁后直接退出（不释放）→ 互斥体被遗弃（模拟上一实例崩溃）
        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                var abandoned = new SingleInstanceLock(name, Logger);
                Assert.True(abandoned.TryAcquire());
                // 故意不 Dispose，线程退出后互斥体被遗弃
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.Null(threadError);

        using var next = new SingleInstanceLock(name, Logger);
        Assert.True(next.TryAcquire());
        Assert.True(next.HasLock);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var lockObj = new SingleInstanceLock(NewAppName(), Logger);
        Assert.True(lockObj.TryAcquire());

        lockObj.Dispose();
        lockObj.Dispose(); // 二次释放不抛
    }
}
