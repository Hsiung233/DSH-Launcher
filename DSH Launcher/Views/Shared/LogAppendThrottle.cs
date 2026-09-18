using System;
using Avalonia.Threading;

namespace DSH_Launcher.Views.Shared
{
    /// <summary>
    /// 日志面板正文刷新的**节流器**:同一时间窗内多次登记只执行一次刷新。
    /// <para>
    /// ⚠ 为什么不能逐行刷新:日志一秒可能来几百行,而两个日志面板都是
    /// "整段文本塞进一个 <c>SelectableTextBlock</c> + <c>TextWrapping=Wrap</c>" 的实现 ——
    /// 每次改 Text 都要**整段重新塑形/排版**(<c>CreateTextLayout</c> → ShapeTextRuns),外加一次 O(n) 字符串复制,
    /// 行数一多就是平方级。实测:<c>dsh web</c> 启动失败刷出 **43 万字符**时,
    /// UI 线程被钉死在 Avalonia 文本排版里,窗口直接"未响应"。
    /// </para>
    /// <para>
    /// 必须是**节流**而不是防抖:防抖(每次到达都重置计时)在持续刷屏时永远不会触发,
    /// 面板反而彻底不刷新了。
    /// </para>
    /// </summary>
    internal sealed class LogAppendThrottle
    {
        /// <summary>默认刷新间隔(200ms:人眼已看不出延迟,又能把一秒钟几百行合并成几次排版)。</summary>
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(200);

        private readonly Action _onTick;
        private readonly TimeSpan _interval;
        private bool _pending;

        /// <param name="onTick">到点时执行的刷新动作(通常是从服务侧现取整段文本赋给面板)。</param>
        /// <param name="interval">刷新间隔;省略则用 <see cref="DefaultInterval"/>。</param>
        public LogAppendThrottle(Action onTick, TimeSpan? interval = null)
        {
            this._onTick = onTick;
            this._interval = interval ?? DefaultInterval;
        }

        /// <summary>
        /// 登记一次刷新:已有待执行的任务时直接合并(这就是节流),
        /// 否则安排一个延迟任务在刷新间隔后执行。
        /// </summary>
        public void Schedule()
        {
            if (this._pending)
            {
                return;
            }

            this._pending = true;

            // Background 优先级:等本轮的布局/渲染做完再刷新,不跟渲染抢时间片
            DispatcherTimer.RunOnce(this.Tick, this._interval, DispatcherPriority.Background);
        }

        private void Tick()
        {
            this._pending = false;
            this._onTick();
        }
    }
}
