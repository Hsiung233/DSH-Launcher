using System;
using System.Collections.Generic;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 命令行参数里“本次是由系统自启动拉起的”这个标记。
    /// <para>
    /// 为什么需要它:系统自启动项写的是 <c>"…\DSH Launcher.exe" --autostart</c>(见 <see cref="AutoStartEntry"/>),
    /// 应用据此把这次启动当成**后台启动** —— 即便用户开着“启动时打开主界面”,也不弹窗,直接收进系统托盘。
    /// 登录时弹一个窗口是自启动功能最容易被用户关掉的原因;托盘图标仍在,想用时点一下就有。
    /// </para>
    /// <para>
    /// 它同时是 app.log 里区分“用户手动启动”与“系统拉起”的唯一证据:自启动不生效时,
    /// 先看日志里有没有这一行,就能判断是**系统没拉起**还是**拉起了但行为不对**。
    /// </para>
    /// <para>
    /// 为什么参数要在 <c>Program.Main</c> 里先存下来:Avalonia 的启动流程会消费掉 args,
    /// 而 App 初始化时已经没有原始参数了(见 Program.Main 的注释)。
    /// </para>
    /// </summary>
    public static class StartupArguments
    {
        /// <summary>自启动项使用的参数。改这里等于改自启动项的写法(<see cref="AutoStartEntry"/> 引用同一常量)。</summary>
        public const string AutoStartSwitch = "--autostart";

        /// <summary>
        /// "显示主界面"启动使用的参数(与 <see cref="AutoStartSwitch"/> 相反:明确要求把主界面叫出来)。
        /// 带它的**二次启动**会走专用管道让已有实例无条件显示主界面
        /// (见 <see cref="SingleInstanceGuard.NotifyShowMainWindow"/>),
        /// 不受"重复启动应用时"设置影响 —— 那项设置配成开 WebView/无动作时,
        /// 普通二次启动根本不会弹主界面,界面自动化就没有确定的入口。
        /// </summary>
        public const string ShowMainWindowSwitch = "--show-main-window";

        /// <summary>本次进程是否由系统自启动拉起。</summary>
        public static bool IsAutoStartLaunch { get; private set; }

        /// <summary>本次进程是否带"显示主界面"标记(通常作为二次启动,通知已有实例后即退出)。</summary>
        public static bool IsShowMainWindowLaunch { get; private set; }

        /// <summary>记录本次进程的命令行参数(只应由 <c>Program.Main</c> 调用一次)。</summary>
        public static void Initialize(IReadOnlyList<string> args)
        {
            IsAutoStartLaunch = ContainsSwitch(args, AutoStartSwitch);
            IsShowMainWindowLaunch = ContainsSwitch(args, ShowMainWindowSwitch);
        }

        /// <summary>
        /// 把当前进程补标为"自启动拉起"(幂等)。只给一个调用方:进程链逃逸重启 ——
        /// 逃逸后的新实例没有命令行参数,靠逃逸标记文件把"逃逸前是自启动"的语义接过来(见 <see cref="CompatChainEscape"/>)。
        /// </summary>
        public static void MarkAutoStartLaunch() => IsAutoStartLaunch = true;

        /// <summary>
        /// 参数里是否带自启动标记。保留这个公开名是给既有调用方/测试用(语义等同
        /// <c>ContainsSwitch(args, AutoStartSwitch)</c>)。
        /// </summary>
        public static bool ContainsAutoStart(IReadOnlyList<string> args) => ContainsSwitch(args, AutoStartSwitch);

        /// <summary>
        /// 参数里是否带指定标记。大小写不敏感(Windows 注册表里的命令行谁写的都可能大小写不一致),
        /// 但**必须整段相等** —— 不能拿 <c>Contains</c> 去撞 <c>--autostart-foo</c> 这种别的参数。
        /// </summary>
        public static bool ContainsSwitch(IReadOnlyList<string> args, string switchName)
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (string.Equals(args[i], switchName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
