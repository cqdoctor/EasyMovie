// ─────────────────────────────────────────────────────────────────────────────
// 一次性探针（非产品代码）：实测「WPF UI 线程上 async void 抛出的异常，
// 是否会被 Dispatcher 的全局未处理异常处理器接住、且进程不崩溃」。
//
// 为什么需要它：项目记忆里写着「async void 102 处，异步异常无法捕获会直接崩进程」，
// 但 App.xaml.cs 已注册 DispatcherUnhandledException 且 args.Handled = true。
// 若异常确实被集中兜住，那 59 处无 try/catch 的 async void 就不是崩溃级缺陷，
// 大规模加 try/catch 属于低收益改动；反之则是必修项。
// 结论不能靠推断——用户要求证据驱动。
//
// 复用的是 AsyncVoidMethodBuilder.SetException 的真实路径：
// 异常 → 捕获的 SynchronizationContext.Post → Dispatcher.BeginInvoke → Dispatcher 未处理异常。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

internal static class Program
{
    private static readonly ConcurrentQueue<string> Log = new ConcurrentQueue<string>();

    [STAThread]
    private static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        int caught = 0;

        // 等价于 App.xaml.cs 里的 DispatcherUnhandledException（底层即 Dispatcher.UnhandledException）
        dispatcher.UnhandledException += (s, e) =>
        {
            Interlocked.Increment(ref caught);
            Log.Enqueue("  [捕获] " + e.Exception.GetType().Name + ": " + e.Exception.Message);
            e.Handled = true;
        };

        // 用例 A：async void 在 await 之后抛出（续体回到 UI 线程的那条路径，风险最高的一种）
        dispatcher.BeginInvoke(new Action(() => CaseA_ThrowAfterAwait()));
        // 用例 B：async void 在首个 await 之前抛出（经 builder 仍走 Post 路径）
        dispatcher.BeginInvoke(new Action(() => CaseB_ThrowBeforeAwait()));
        // 用例 C：普通同步委托抛异常（基线，验证兜底本身生效）
        dispatcher.BeginInvoke(new Action(() => CaseC_SyncThrow()));

        // 收尾：等所有抛错跑完，确认 dispatcher 还活着
        dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1200);
            Log.Enqueue("  [存活] 三次抛错后 dispatcher 仍在正常运行（未崩溃）");
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        });

        Dispatcher.Run();

        Console.WriteLine("=== 探针结果 ===");
        foreach (var line in Log) Console.WriteLine(line);
        Console.WriteLine("捕获次数 = " + caught + " / 3");
        Console.WriteLine(caught == 3
            ? "结论：async void 的异常**会被** Dispatcher 全局兜住，进程不崩。"
            : "结论：存在未被兜住的抛错路径，需逐个加 try/catch。");
        return 0;
    }

    private static async void CaseA_ThrowAfterAwait()
    {
        await Task.Delay(50);
        throw new InvalidOperationException("A: async void 在 await 之后抛出");
    }

    private static async void CaseB_ThrowBeforeAwait()
    {
        throw new InvalidOperationException("B: async void 在首个 await 之前抛出");
    }

    private static void CaseC_SyncThrow()
        => throw new InvalidOperationException("C: 普通同步委托抛出（基线）");
}
