using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace DSH_Launcher.Views.Shared
{
    /// <summary>
    /// "复制到剪贴板 + 按钮文字短暂变成「已复制」"这一组反馈。
    /// <para>
    /// 首页与插件页原先各写了一份(两对方法加一个忙碌标志),这里收拢成一处:
    /// 剪贴板 API 的正确写法(Avalonia 12 用 <c>DataTransfer</c>+<c>SetDataAsync</c>)只需要维护一遍。
    /// </para>
    /// <para>
    /// 忙碌标志不是可选项:连点时若并发改同一个按钮文字,还原顺序一乱
    /// 就会永久停在"已复制"(或文字来回跳)。
    /// </para>
    /// </summary>
    internal sealed class CopyFeedback
    {
        /// <summary>反馈文案停留时长。</summary>
        private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(1500);

        private readonly TextBlock _label;
        private readonly string _originalText;
        private bool _busy;

        /// <param name="label">要临时改文字的按钮内文本块。</param>
        /// <param name="originalText">反馈结束后要还原的文字。</param>
        public CopyFeedback(TextBlock label, string originalText)
        {
            this._label = label;
            this._originalText = originalText;
        }

        /// <summary>
        /// 把 <paramref name="text"/> 写入剪贴板,成功后把按钮文字闪成"已复制"。
        /// 剪贴板不可用或写入失败时**静默返回** —— 这条路径上没有用户能采取的动作,
        /// 弹错误只会打扰人。
        /// </summary>
        /// <param name="owner">用于取 <see cref="TopLevel"/> 的控件(传页面本身即可)。</param>
        public async Task CopyAsync(Visual owner, string text)
        {
            if (!await TrySetTextAsync(owner, text))
            {
                return;
            }

            await this.FlashAsync();
        }

        private static async Task<bool> TrySetTextAsync(Visual owner, string text)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
                if (clipboard is null)
                {
                    return false;
                }

                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(text));
                await clipboard.SetDataAsync(transfer);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>把按钮文字临时换成"已复制"(同一时刻只允许一个反馈在跑)。</summary>
        private async Task FlashAsync()
        {
            if (this._busy)
            {
                return;
            }

            this._busy = true;
            try
            {
                this._label.Text = "已复制";
                await Task.Delay(HoldDuration);
            }
            finally
            {
                // 无论中间出什么岔子都要还原,否则按钮会永久停在"已复制"
                this._label.Text = this._originalText;
                this._busy = false;
            }
        }
    }
}
