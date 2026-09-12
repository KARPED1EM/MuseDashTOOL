using Avalonia;
using System;

namespace MdModManager;

sealed class Program
{
    // 初始化代码
    [STAThread]
    public static void Main(string[] args)
    {
        bool createdNew;
        System.Threading.Mutex? mutex = null;
        try
        {
            // 使用当前会话的互斥锁，避免受限账户没有 Global 命名空间权限时被误判为已有实例。
            mutex = new System.Threading.Mutex(true, "Local\\MuseDashTOOL-SingleInstance", out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // 无法创建互斥锁时仍应允许主窗口启动，不能将权限问题伪装成已有实例。
            createdNew = true;
        }

        if (!createdNew)
        {
            // 如果有参数则发送参数否则发送激活指令以唤醒主窗口
            var sendArgs = (args != null && args.Length > 0) ? args : new[] { "musedashtool://activate" };
            Bootstrapper.SendArgsToPrimaryInstance(sendArgs);
            return;
        }

        Bootstrapper.StartDeepLinkPipeServer();
        
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Bootstrapper.StopDeepLinkPipeServer();
            mutex?.Dispose();
        }
    }

    // Avalonia 配置
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 部分旧显卡驱动会在 ANGLE 创建首个窗口时卡住，使用软件渲染保证窗口能创建。
            .With(new Avalonia.Win32PlatformOptions
            {
                RenderingMode = [Avalonia.Win32RenderingMode.Software],
                CompositionMode = [Avalonia.Win32CompositionMode.RedirectionSurface]
            })
            .WithInterFont()
            .LogToTrace();

        return builder;
    }
}
