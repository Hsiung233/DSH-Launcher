namespace DSH_Launcher.Services
{
    /// <summary>
    /// dsh 全局包的安装来源。
    /// <para>
    /// 为什么要区分:环境检测原先只问 <c>npm ls -g</c>,而 npm 与 pnpm 的全局目录是两套 ——
    /// 用 pnpm 全局装的机器上那条命令看不到包,于是界面显示"未安装"、自动运行被跳过。
    /// 判定出来源之后,安装/更新也跟随来源走,避免 pnpm 装的包被 npm 又装一份(两份 shim
    /// 互相遮蔽,升级后版本对不上)。
    /// </para>
    /// </summary>
    internal enum DshInstaller
    {
        /// <summary>未能判定(volta / scoop / 手动解压等);安装与更新按 npm 处理(= 旧行为)。</summary>
        Unknown = 0,

        /// <summary>npm 全局安装(<c>npm install -g</c>)。</summary>
        Npm = 1,

        /// <summary>pnpm 全局安装(<c>pnpm add -g</c>)。</summary>
        Pnpm = 2,
    }

    /// <summary><see cref="DshInstaller"/> 的展示文本(界面与日志共用,避免两处各写一套措辞)。</summary>
    internal static class DshInstallerText
    {
        public static string Describe(this DshInstaller installer) => installer switch
        {
            DshInstaller.Npm => "npm 全局安装",
            DshInstaller.Pnpm => "pnpm 全局安装",
            _ => "安装来源未知",
        };
    }
}
