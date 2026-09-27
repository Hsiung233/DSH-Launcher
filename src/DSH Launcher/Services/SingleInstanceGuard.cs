using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 单实例协调:互斥体判定"首个实例",命名管道通知已有实例"又启动了一次"。
    /// <para>
    /// 用命名管道而非命名 <c>EventWaitHandle</c>:后者仅 Windows 支持,macOS/Linux 上创建即抛
    /// <c>PlatformNotSupportedException</c>;命名管道全平台可用(Unix 上映射为 Unix 域套接字)。
    /// </para>
    /// <para>
    /// 管道只是"重复启动了一次"的信号,**不带载荷**:该做什么由已有实例按自己的
    /// "重复启动应用时"设置决定(见 <c>App.OnRepeatLaunchRequested</c>)——两个实例读的是同一份设置文件,
    /// 让监听方能改设置即时生效,也免得给这段本就微妙的代码再加一层协议。
    /// </para>
    /// <para>
    /// ⚠ 锁的释放/取回是有语义的,不要当成普通资源清理:
    /// 进程链逃逸(见 <see cref="CompatChainEscape"/>)必须**先放锁、再由 explorer.exe 拉起新实例**,
    /// 否则新实例会走"检测到已有实例"这条路被误退;而逃逸失败时必须把锁**拿回来**,
    /// 否则本实例就成了"没有锁的实例",之后任何一次启动都会成功创建第二个实例
    /// (两个托盘图标、各自都能起 dsh)。
    /// </para>
    /// </summary>
    internal sealed class SingleInstanceGuard : IDisposable
    {
        private const string MutexName = "DSH_Launcher_SingleInstance";

        /// <summary>
        /// 重复启动通知用的管道名。
        /// ⚠ 字面量**不要跟着标识符改**:新旧版本同时在机器上时(例如开发版 + 已安装版),
        /// 管道名不一致就会互相通知不到 —— 旧实例占着互斥体、新实例又通知不了它,
        /// 用户看到的就是"点了没反应"。该字符串从首版起就是这个值。
        /// </summary>
        private const string RepeatLaunchPipeName = "DSH_Launcher_ShowMainWindow";

        /// <summary>
        /// "无条件显示主界面"通知用的管道名(同样:字面量从引入起就不要改)。
        /// 与 <see cref="RepeatLaunchPipeName"/> 分开的理由:那个信号的响应是**用户设置**说了算
        /// ("重复启动应用时"可能是开 WebView/无动作),而界面自动化、热键工具这类调用方
        /// 要的是确定性 —— 这条管道收到连接就**总是**把主界面叫出来,与设置无关。
        /// 对应启动参数 <see cref="StartupArguments.ShowMainWindowSwitch"/>。
        /// </summary>
        private const string ForceShowPipeName = "DSH_Launcher_ForceShowWindow";

        /// <summary>接管"正在退出的上一个实例"时最多重试的次数与间隔(见 <see cref="TryTakeOver"/>)。</summary>
        private const int TakeOverAttempts = 6;
        private const int TakeOverDelayMs = 250;

        private Mutex? _mutex;

        private SingleInstanceGuard()
        {
        }

        /// <summary>是否由本实例持有单实例所有权(为 false 表示已有实例在运行,调用方应通知它并退出)。</summary>
        public bool IsPrimary { get; private set; }

        /// <summary>
        /// 尝试取得单实例所有权。
        /// 已存在实例且是"正在退出"的短暂窗口时,会再争取一次(见 <see cref="TryTakeOver"/>)。
        /// </summary>
        public static SingleInstanceGuard Acquire()
        {
            var guard = new SingleInstanceGuard();
            var mutex = new Mutex(true, MutexName, out var createdNew);
            guard._mutex = mutex;

            guard.IsPrimary = createdNew || guard.TryTakeOver();
            return guard;
        }

        /// <summary>
        /// 通知已在运行的实例"又启动了一次"(连接上即视为信号,不写数据)。
        /// 该实例按自己的"重复启动应用时"设置响应。
        /// 连接失败(已有实例可能正在退出)不算错误 —— 调用方接着退出自己即可。
        /// </summary>
        public void NotifyExistingInstance()
        {
            try
            {
                using var client = new NamedPipeClientStream(".", RepeatLaunchPipeName, PipeDirection.Out);
                client.Connect(1000);
                AppLogService.Write("[启动] 检测到已有实例,已通知其处理本次重复启动");
            }
            catch (Exception)
            {
                // 连接失败(已有实例可能正在退出)时直接退出,不影响已有实例
            }
        }

        /// <summary>
        /// 通知已在运行的实例**无条件显示主界面**(连接上即视为信号,不写数据)。
        /// 与 <see cref="NotifyExistingInstance"/> 的区别:那个按"重复启动应用时"设置响应
        /// (用户可能配的是开 WebView/无动作,主界面根本不出来),这个总是把主界面叫出来。
        /// 连接失败同样不算错误。
        /// </summary>
        public void NotifyShowMainWindow()
        {
            try
            {
                using var client = new NamedPipeClientStream(".", ForceShowPipeName, PipeDirection.Out);
                client.Connect(1000);
                AppLogService.Write("[启动] 收到显示主界面请求,已通知已有实例显示主界面");
            }
            catch (Exception)
            {
                // 连接失败(已有实例可能正在退出)时直接退出,不影响已有实例
            }
        }

        /// <summary>
        /// 后台监听"重复启动"通知(来自二次启动的进程)。
        /// 命名管道服务端一次只能服务一个连接,每接受一个连接就重建一次监听;
        /// 连接本身即信号,无需读取数据。<paramref name="onRepeatLaunchRequested"/> 在后台线程触发。
        /// </summary>
        public Task ListenAsync(CancellationToken cancellationToken, Action onRepeatLaunchRequested)
            => this.ListenOnPipeAsync(RepeatLaunchPipeName, "重复启动", cancellationToken, onRepeatLaunchRequested);

        /// <summary>
        /// 后台监听"无条件显示主界面"通知(来自带 <see cref="StartupArguments.ShowMainWindowSwitch"/>
        /// 的二次启动,或直接连管道的自动化脚本)。回调在后台线程触发,调用方负责调度 UI 线程。
        /// </summary>
        public Task ListenShowWindowAsync(CancellationToken cancellationToken, Action onShowMainWindow)
            => this.ListenOnPipeAsync(ForceShowPipeName, "显示主界面", cancellationToken, onShowMainWindow);

        private async Task ListenOnPipeAsync(
            string pipeName, string label, CancellationToken cancellationToken, Action onSignal)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken);
                    onSignal();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 监听异常不致命:稍后重建管道重试;退避一下,避免高频失败刷满 CPU
                    AppLogService.Write($"[启动] 单实例管道({label})监听异常: {ex.Message}");
                    try
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 释放单实例所有权(进程链逃逸前必须调用,理由见类注释)。
        /// 释放失败不阻断逃逸:极端情况下新实例会走通知路径退出,用户手动启动即可恢复。
        /// </summary>
        public void Release()
        {
            try
            {
                this._mutex?.ReleaseMutex();
            }
            catch (Exception)
            {
                // 忽略:见方法注释
            }

            this.Dispose();
        }

        /// <summary>
        /// 逃逸失败后的收尾:把所有权重新拿回来(期间已经 Release/Dispose 过)。
        /// 拿不回来只记日志 —— 极端情况下用户会看到两个实例,退出一个即可。
        /// </summary>
        public void TryReacquire()
        {
            try
            {
                var mutex = new Mutex(true, MutexName, out var createdNew);
                this._mutex = mutex;
                this.IsPrimary = createdNew;
                AppLogService.Write(createdNew
                    ? "[启动] 已重新取得单实例所有权。"
                    : "[启动] 单实例互斥体已被别的实例占用(本次未取回)。");
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[启动] 重新取得单实例所有权失败:{ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                this._mutex?.Dispose();
            }
            catch (Exception)
            {
                // 释放失败不影响后续流程
            }

            this._mutex = null;
        }

        /// <summary>
        /// 在"已有实例正在退出"的短暂窗口里再争取一次所有权。
        /// <para>
        /// 为什么需要:进程链逃逸时旧实例要先 <c>ReleaseMutex</c> 再让 explorer 拉起新实例,
        /// 两个动作之间有几百毫秒;若这期间有别的实例抢先拿到互斥体,逃逸出来的实例会走
        /// "通知已有实例并退出"这条路 —— 用户看到的就是"点了没反应"。
        /// 命中被遗弃的互斥体(<c>AbandonedMutexException</c>)算作"已获得",等于接管它。
        /// </para>
        /// </summary>
        private bool TryTakeOver()
        {
            for (var i = 0; i < TakeOverAttempts; i++)
            {
                Thread.Sleep(TakeOverDelayMs);
                try
                {
                    if (this._mutex?.WaitOne(0) == true)
                    {
                        AppLogService.Write($"[启动] 等待 {(i + 1) * TakeOverDelayMs} 毫秒后取得单实例所有权(上一个实例正在退出)");
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // 上一个实例没来得及释放就退出了:这个异常本身表示所有权已归本进程
                    AppLogService.Write("[启动] 接管了被遗弃的单实例互斥体");
                    return true;
                }
                catch (Exception)
                {
                    // 其它异常按"未取得"继续重试
                }
            }

            return false;
        }
    }
}
