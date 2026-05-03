using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.App.Resources;
using SIDM.Core;

namespace SIDM.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public DownloadsViewModel Downloads { get; }

    public MainViewModel(DownloadsViewModel downloads)
    {
        Downloads = downloads;
    }

    public string Title => string.Format(Strings.Main_Greeting_Format, AppInfo.DisplayName, AppInfo.Version);
}
