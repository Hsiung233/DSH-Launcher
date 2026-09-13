using System;
using Avalonia.Controls;

namespace DSH_Launcher.Controls
{
    /// <summary>
    /// <see cref="NumericUpDown"/> 的补充:当值处于空状态(显示占位文本)时点增减按钮,
    /// 从 <see cref="DefaultValue"/> 起算,而不是控件原生的 Minimum(增)/Maximum(减)。
    ///
    /// 原生行为见 Avalonia 源码 <c>NumericUpDown.OnIncrement/OnDecrement</c>:
    /// 值为空时增取 <c>Minimum</c>、减取 <c>Maximum</c>,对于“留空即默认值”的语义会造成困扰
    /// (例如留空后点一次减号会直接跳到最大值)。
    /// </summary>
    public class NumericUpDownWithDefault : NumericUpDown
    {
        /// <summary>
        /// 空状态下点增减按钮的起算值。未设置(null)时保持控件原生行为。
        /// </summary>
        public decimal? DefaultValue { get; set; }

        /// <summary>
        /// 复用 <see cref="NumericUpDown"/> 的样式(含 ControlTheme 与模板)。
        /// 必须重写:否则 StyleKey 是本派生类型,Fluent 主题里针对 NumericUpDown 的 ControlTheme 命不中,
        /// 控件没有模板(缺少 PART_TextBox 与增减按钮,只剩一个空壳)。
        /// </summary>
        protected override Type StyleKeyOverride => typeof(NumericUpDown);

        /// <summary>
        /// 先把空值落成默认值,再交给基类做 ±Increment,使空状态下的第一次旋转从默认值出发。
        /// </summary>
        protected override void OnSpin(SpinEventArgs e)
        {
            if (Value is null && this.DefaultValue is decimal seed)
            {
                // 默认值也要受 Minimum/Maximum 约束,避免落一个越界值
                SetCurrentValue(ValueProperty, Math.Clamp(seed, Minimum, Maximum));
            }

            // 基类会在此基础上 ±Increment 并夹取范围,同时触发 Spinned
            base.OnSpin(e);
        }
    }
}
