using Avalonia;
using System;
using DSH_Launcher.Services;

namespace DSH_Launcher;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 命令行参数要在这里先记下来:Avalonia 的启动流程会消费掉 args,
        // 而 App 初始化时已经没有原始参数了 —— 判断“本次是不是系统自启动拉起的”只依赖这一处(见 StartupArguments)。
        StartupArguments.Initialize(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
