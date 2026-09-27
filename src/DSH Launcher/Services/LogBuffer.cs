using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 有上限的日志缓冲区(线程安全)。
    ///
    /// <para>
    /// 为什么必须限长:两个日志面板都是"把整段日志塞进一个 <c>SelectableTextBlock</c> + <c>TextWrapping=Wrap</c>"
    /// 的实现,文本一改就要整段重新塑形/排版(不是按行增量)。而 dsh / npm / pnpm 出错时会刷屏 ——
    /// 实测一次 <c>dsh web</c> 启动失败吐出了 **43 万字符**(Node 把整个 AggregateError 树完整打印),
    /// 面板逐行追加的结果是 UI 线程长时间钉死在 Avalonia 文本排版里,窗口直接"未响应"。
    /// 所以长度必须在**源头**钉住(顺带也把内存占用钉住);界面侧另有刷新节流配合。
    /// </para>
    ///
    /// <para>
    /// 裁剪策略:超过上限时**从头部按整行丢弃**(绝不把一行切成两半),并把丢弃的字符数累计起来,
    /// 读取时在最前面补一行"已省略"提示 —— 用户看到的始终是完整的**尾部**日志,
    /// 而日志面板本来就是实时尾部视图(完整历史另有 app.log)。
    /// </para>
    /// </summary>
    public sealed class LogBuffer
    {
        /// <summary>
        /// 默认字符数上限。两个日志面板(首页 / 插件页)历史上各写了一份同值的常量,
        /// 现在唯一定义在此 —— 这个值只由"界面能被多长的文本拖慢"决定,与哪个服务无关。
        /// </summary>
        public const int DefaultMaxChars = 50_000;

        /// <summary>"已省略"提示行的格式(补在保留内容的开头)。</summary>
        private const string OmittedNoteFormat = "…… 日志过长,已省略更早的 {0} 字符 ……\r\n";

        /// <summary>裁剪时一次性多腾出的余量(上限的 1/8):否则每追加一行都要再裁一次。</summary>
        private const int TrimHeadroomDivisor = 8;

        /// <summary>为对齐行首最多向后多找的字符数(避免遇到"一整行超长文本"时把缓冲区丢空)。</summary>
        private const int LineSnapLimitChars = 512;

        private readonly StringBuilder _builder = new();
        private readonly Lock _lock = new();
        private readonly int _maxChars;
        private int _omittedChars;

        public LogBuffer(int maxChars) => this._maxChars = maxChars;

        /// <summary>当前保留的字符数(不含"已省略"提示行)。</summary>
        public int Length
        {
            get
            {
                lock (this._lock)
                {
                    return this._builder.Length;
                }
            }
        }

        /// <summary>追加文本(可含多行),超出上限时立即裁剪。</summary>
        public void Append(string text)
        {
            lock (this._lock)
            {
                this._builder.Append(text);
                this.Trim();
            }
        }

        public void Clear()
        {
            lock (this._lock)
            {
                this._builder.Clear();
                this._omittedChars = 0;
            }
        }

        /// <summary>
        /// 从指定下标读到末尾。下标会被夹到有效范围:若它指向已被裁掉的区域,就从保留内容的开头返回
        /// (错误信息里因此只会展示仍在缓冲区里的那段输出)。
        /// </summary>
        public string ReadFrom(int start)
        {
            lock (this._lock)
            {
                return this.Compose(Math.Clamp(start, 0, this._builder.Length));
            }
        }

        public override string ToString()
        {
            lock (this._lock)
            {
                return this.Compose(0);
            }
        }

        /// <summary>锁内调用:拼出"已省略提示 + [from, 末尾)"。</summary>
        private string Compose(int from)
        {
            var note = this._omittedChars > 0
                ? string.Format(CultureInfo.InvariantCulture, OmittedNoteFormat, this._omittedChars)
                : string.Empty;

            return note + this._builder.ToString(from, this._builder.Length - from);
        }

        /// <summary>
        /// 锁内调用:超过上限时从头部丢弃。
        /// 丢弃量 = 超出部分 + 一点余量,再向后对齐到行首。
        /// ⚠ 在 <see cref="LineSnapLimitChars"/> 窗口内找不到换行时,必须**按原位置切**而不是继续切到窗口末尾:
        /// 否则"缓冲区整段是一行超长文本"(例如只有 \r 没有 \n 的进度输出)会被整个丢空,
        /// 面板只剩一行"已省略 N 字符",看起来像日志没了。按原位置切只是切在行中间,内容还在。
        /// </summary>
        private void Trim()
        {
            var excess = this._builder.Length - this._maxChars;
            if (excess <= 0)
            {
                return;
            }

            var target = excess + (this._maxChars / TrimHeadroomDivisor);
            var limit = Math.Min(this._builder.Length, target + LineSnapLimitChars);

            var cut = target;
            while (cut < limit && this._builder[cut] != '\n')
            {
                cut++;
            }

            if (cut >= this._builder.Length || this._builder[cut] != '\n')
            {
                // 窗口内没有换行:退回原位置(宁可切在行中间,也不把缓冲区丢空)
                cut = target;
            }
            else
            {
                cut++; // 换行本身也丢掉,保留内容从下一行行首开始
            }

            this._builder.Remove(0, cut);
            this._omittedChars += cut;
        }
    }
}
