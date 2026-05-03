using SIDM.App.ViewModels;
using Wpf.Ui.Controls;

namespace SIDM.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.Downloads.LoadAsync();
    }
}
