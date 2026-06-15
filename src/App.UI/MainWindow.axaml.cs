using Avalonia.Controls;
using App.UI.ViewModels;
using App.UI.Views;

namespace App.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 设置主页 ViewModel 作为 DataContext
        // 后续 Phase 1.3 会通过 DI 注入
        DataContext = new HomeViewModel();
    }
}
