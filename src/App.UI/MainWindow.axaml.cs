using Avalonia.Controls;
using App.UI.ViewModels;

namespace App.UI;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel shellViewModel)
    {
        InitializeComponent();
        DataContext = shellViewModel;
    }
}
