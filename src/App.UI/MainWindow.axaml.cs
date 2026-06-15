using Avalonia.Controls;
using App.UI.ViewModels;

namespace App.UI;

public partial class MainWindow : Window
{
    public MainWindow(HomeViewModel homeViewModel)
    {
        InitializeComponent();
        DataContext = homeViewModel;
    }
}
