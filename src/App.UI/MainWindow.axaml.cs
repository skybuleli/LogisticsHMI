using Avalonia.Controls;
using App.UI.ViewModels;

namespace App.UI;

public partial class MainWindow : Window
{
    /// <summary>
    /// XAML 设计器/运行时加载器使用的无参构造函数。
    /// 实际运行时由 DI 容器通过有参构造函数实例化。
    /// </summary>
    [Obsolete("仅用于 XAML 设计器")]
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// DI 容器使用的主构造函数——注入 ShellViewModel 作为全局 DataContext。
    /// </summary>
    public MainWindow(ShellViewModel shellViewModel)
    {
        InitializeComponent();
        DataContext = shellViewModel;
    }
}
