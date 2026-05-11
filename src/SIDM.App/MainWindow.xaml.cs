using System.Windows;
using SIDM.App.Services;
using SIDM.App.ViewModels;
using SIDM.App.Views;
using Wpf.Ui.Controls;

namespace SIDM.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly OnboardingService _onboarding;
    private readonly IServiceProvider _services;

    public MainWindow(MainViewModel viewModel, OnboardingService onboarding, IServiceProvider services)
    {
        _viewModel = viewModel;
        _onboarding = onboarding;
        _services = services;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.Downloads.LoadAsync();
            await MaybeShowWelcomeAsync();
        };
    }

    private async Task MaybeShowWelcomeAsync()
    {
        if (!await _onboarding.ShouldShowWelcomeAsync()) return;

        var welcome = new WelcomeDialog { Owner = this };
        welcome.ShowDialog();
        await _onboarding.MarkCompletedAsync();

        if (welcome.ShouldOpenSettings)
        {
            // Reuse the existing Settings command on the downloads VM so
            // there's a single code path for "open the settings dialog."
            _viewModel.Downloads.OpenSettingsCommand.Execute(null);
        }
    }
}
