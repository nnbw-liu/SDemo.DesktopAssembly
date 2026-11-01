using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Install;
using SDemo.Shell.Processes;
using SDemo.Shell.Registry;
using SDemo.Shell.Wpf.Widgets;

namespace SDemo.Shell.Wpf.Panel;

/// <summary>
/// 壳面板（DA-98 / DA-100）：组件管理 + 设置入口 + 诊断；不承载聊天或业务视图。
/// 关闭面板只回托盘，不退出进程。
/// </summary>
public sealed class ShellPanelWindow : Window
{
    private readonly AppRegistry _registry;
    private readonly AppProcessManager _manager;
    private readonly AppInstaller _installer;
    private readonly WidgetController _controller;
    private readonly ShellPaths _paths;
    private readonly IShellLog _log;
    private readonly StackPanel _rows = new();
    private readonly Border _emptyPanel;
    private readonly TextBlock _statusLine = new();
    private bool _allowClose;

    public ShellPanelWindow(
        AppRegistry registry,
        AppProcessManager manager,
        AppInstaller installer,
        WidgetController controller,
        ShellPaths paths,
        IShellLog? log = null)
    {
        _registry = registry;
        _manager = manager;
        _installer = installer;
        _controller = controller;
        _paths = paths;
        _log = log ?? NullShellLog.Instance;

        Title = $"SDemo DesktopAssembly {HostVersion.Product}";
        Width = 680;
        Height = 620;
        MinWidth = 560;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1E));
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0));
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        _emptyPanel = BuildEmptyPanel();
        BuildLayout();

        _registry.Changed += OnRegistryChanged;
        Closing += (_, args) =>
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            Hide();
        };
    }

    public event EventHandler? ExitRequested;

    public void ShowPanel()
    {
        Refresh();
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    public void Refresh()
    {
        _rows.Children.Clear();
        var entries = _registry.Entries;
        _emptyPanel.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in entries)
        {
            _rows.Children.Add(BuildRow(entry));
        }

        var invalid = _registry.InvalidCount;
        _statusLine.Text = invalid > 0
            ? $"组件 {entries.Count} 个 · {invalid} 个加载失败（查看下方原因） · {_paths.AppsDirectory}"
            : $"组件 {entries.Count} 个 · 上限 {_registry.ComponentLimit} · {_paths.AppsDirectory}";
        _statusLine.Foreground = new SolidColorBrush(invalid > 0
            ? Color.FromRgb(0xF8, 0x71, 0x71)
            : Color.FromRgb(0x8A, 0x8A, 0x98));
    }

    private void BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = "桌面外壳",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = $"SDemo DesktopAssembly {HostVersion.Product} · hostApi {ShellConstants.HostApiVersion} · 组件与窗口归壳，数据归应用",
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xAA)),
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _rows,
        };

        var content = new Grid();
        content.Children.Add(scroll);
        content.Children.Add(_emptyPanel);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new WrapPanel();
        buttons.Children.Add(CreateButton("安装应用（zip）", (_, _) => InstallFromZip()));
        buttons.Children.Add(CreateButton("打开安装目录", (_, _) => OpenFolder(_paths.AppsDirectory)));
        buttons.Children.Add(CreateButton("全部启动", (_, _) => _controller.ShowAll()));
        buttons.Children.Add(CreateButton("隐藏全部", (_, _) => _controller.HideAll()));
        buttons.Children.Add(CreateButton("复制诊断", (_, _) => CopyDiagnostics()));
        buttons.Children.Add(CreateButton("退出外壳", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
        footer.Children.Add(buttons);
        _statusLine.FontSize = 11;
        _statusLine.Margin = new Thickness(0, 10, 0, 0);
        _statusLine.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_statusLine);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    private Border BuildEmptyPanel()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "还没有安装任何应用",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "把一个应用安装包（zip）放到安装目录后点击「安装应用」，壳会扫描组件清单、校验哈希并把组件摆到桌面。",
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xAA)),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"应用数据目录（卸载不删）：{ShellPaths.AppDataDirectoryFor("<appId>")}",
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x80)),
        });

        var actionRow = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        actionRow.Children.Add(CreateButton("安装应用（zip）", (_, _) => InstallFromZip()));
        actionRow.Children.Add(CreateButton("打开安装目录", (_, _) => OpenFolder(_paths.AppsDirectory)));
        panel.Children.Add(actionRow);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x2A, 0x2E, 0x3A)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Margin = new Thickness(0, 10, 0, 10),
            Child = panel,
        };
    }

    private Border BuildRow(AppEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = $"{entry.Manifest.Name}  ·  {entry.AppId}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });

        var stateText = entry.LastError is { } error
            ? $"{StateLabel(entry.State)} · {error.Code}: {error.Message}"
            : $"{StateLabel(entry.State)} · {entry.WidgetId} · pid {entry.Runtime?.Pid.ToString() ?? "-"} · 端口 {entry.Runtime?.Port.ToString() ?? "-"}";
        info.Children.Add(new TextBlock
        {
            Text = stateText,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(entry.LastError is not null
                ? Color.FromRgb(0xF8, 0x71, 0x71)
                : Color.FromRgb(0x9A, 0x9A, 0xAA)),
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var actions = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(CreateToggle("显示", entry.Visible, (_, _) =>
        {
            if (entry.Visible)
            {
                _controller.HideWidget(entry.WidgetId);
            }
            else
            {
                _controller.ShowWidget(entry.WidgetId);
            }

            Refresh();
        }));
        actions.Children.Add(CreateToggle("置顶", entry.AlwaysOnTop, (_, _) =>
        {
            _controller.ToggleAlwaysOnTop(entry.WidgetId);
            Refresh();
        }));
        actions.Children.Add(CreateToggle("穿透", entry.ClickThrough, (_, _) =>
        {
            _controller.ToggleClickThrough(entry.WidgetId);
            Refresh();
        }));
        actions.Children.Add(CreateButton("重启", (_, _) =>
        {
            _controller.RestartWidget(entry.WidgetId);
            Refresh();
        }));
        actions.Children.Add(CreateButton("卸载", (_, _) => Uninstall(entry)));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private static Button CreateButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x3A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        button.Click += handler;
        return button;
    }

    private static System.Windows.Controls.Primitives.ToggleButton CreateToggle(
        string text,
        bool isChecked,
        RoutedEventHandler handler)
    {
        var toggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = text,
            IsChecked = isChecked,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x3A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        toggle.Click += (sender, args) =>
        {
            handler(sender, args);
            toggle.IsChecked = isChecked;
        };
        return toggle;
    }

    private static string StateLabel(string state) => state switch
    {
        AppStates.Ready => "运行中",
        AppStates.Launching => "启动中",
        AppStates.Idle => "空闲",
        AppStates.Stopped => "未运行",
        AppStates.Failed => "错误",
        AppStates.Invalid => "不可用",
        _ => state,
    };

    private void InstallFromZip()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择应用安装包",
            Filter = "应用安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var result = _installer.InstallFromZip(dialog.FileName);
        if (!result.Success)
        {
            MessageBox.Show(this, result.Message ?? "安装失败。", "安装应用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _statusLine.Text = $"已安装 {result.AppId} → {result.InstalledPath}";
        if (result.AppId is not null && _registry.Find(result.AppId) is { } installed)
        {
            _controller.ShowWidget(installed.WidgetId);
        }

        Refresh();
    }

    private async void Uninstall(AppEntry entry)
    {
        var answer = MessageBox.Show(
            this,
            $"卸载 {entry.Manifest.Name}？\n\n只删除程序目录，用户数据保留在：\n{ShellPaths.AppDataDirectoryFor(entry.AppId)}",
            "卸载应用",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _manager.StopAppAsync(entry.AppId);
        var result = _installer.Uninstall(entry.AppId);
        if (!result.Success)
        {
            MessageBox.Show(this, result.Message ?? "卸载失败。", "卸载应用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _statusLine.Text = $"已卸载 {entry.AppId}；数据保留在 {result.DataDirectory}";
        Refresh();
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(_controller.BuildDiagnosticsText());
            _statusLine.Text = "诊断信息已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            _log.Warn("复制诊断信息失败。", ex);
            _statusLine.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRegistryChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);
}

