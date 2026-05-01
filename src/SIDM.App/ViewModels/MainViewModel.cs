using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.App.Resources;
using SIDM.Core;

namespace SIDM.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _greeting = string.Format(Strings.Main_Greeting_Format, AppInfo.DisplayName, AppInfo.Version);

    [ObservableProperty]
    private string _tagline = Strings.Main_Tagline;
}
