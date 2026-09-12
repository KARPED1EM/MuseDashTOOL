using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MdModManager.Services;
using System;

namespace MdModManager.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        try
        {
            InitializeComponent();
            RuntimeLog.Write("WelcomeView", "欢迎页视图已创建。");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("WelcomeView", $"欢迎页视图创建失败：{ex}");
            Content = new TextBlock
            {
                Text = "欢迎页加载失败，请查看运行日志。",
                Margin = new Avalonia.Thickness(24)
            };
        }
    }
}
