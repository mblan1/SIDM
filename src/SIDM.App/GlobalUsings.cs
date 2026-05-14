// Enabling <UseWindowsForms>true</UseWindowsForms> brings
// System.Windows.Forms.Application into scope alongside System.Windows.Application
// (the WPF base class), which collides everywhere we touch Application.Current.
// Aliasing Application -> WPF resolves it globally so we don't have to qualify
// every reference; TrayIconService is the only WinForms-using file and it
// imports WinForms via a local using alias.
global using Application = System.Windows.Application;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using Clipboard = System.Windows.Clipboard;
