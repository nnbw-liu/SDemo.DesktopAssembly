using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using SDemo.Hub;

namespace SDemo.Hub.App;

public sealed class MainWindow : Window
{
    public string AppsDirectory { get; }

    public HubUiState State { get; }

    public MainWindow(
        HubPaths paths,
        HubAppRegistry registry,
        HubAppDiscovery discovery,
        HubConnectionMonitor monitor,
        WidgetHostService widgets,
        HubServer hubServer,
        HubSettingsStore settings)
    {
        Title = "SDemo Hub";
        Width = 1180;
        Height = 760;
        MinWidth = 960;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x18));
        AppsDirectory = paths.AppsDirectory;
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/sdemo-hub.png"));
        }
        catch
        {
            // 图标资源缺失不应阻止启动。
        }

        var state = new HubUiState(paths, registry, discovery, monitor, widgets, hubServer, settings);
        State = state;

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddSingleton(state);

        var blazor = new BlazorWebView
        {
            HostPage = "wwwroot/index.html",
            Services = services.BuildServiceProvider(),
        };
        blazor.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Components.Main),
        });

        Content = new Grid
        {
            Children =
            {
                blazor,
            },
        };

        SourceInitialized += (_, _) => ApplyWindowTheme();
        state.Changed += OnStateChanged;
        Closed += (_, _) => state.Changed -= OnStateChanged;
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(ApplyWindowTheme);

    private void ApplyWindowTheme()
    {
        var dark = State.Theme == "dark" || (State.Theme == "auto" && IsSystemDark());
        Background = new SolidColorBrush(dark
            ? Color.FromRgb(0x0E, 0x12, 0x18)
            : Color.FromRgb(0xF4, 0xF6, 0xFA));

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = dark ? 1 : 0;
        try
        {
            // DWMWA_USE_IMMERSIVE_DARK_MODE：Win10 20H1+ 为 20，早期预览为 19。
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // DWM 不可用时保持系统标题栏。
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
