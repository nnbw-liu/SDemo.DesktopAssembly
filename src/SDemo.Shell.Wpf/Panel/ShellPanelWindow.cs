using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Install;
using SDemo.Shell.Processes;
using SDemo.Shell.Registry;
using SDemo.Shell.Settings;
using SDemo.Shell.Storage;
using SDemo.Shell.Wpf.Settings;
using SDemo.Shell.Wpf.Widgets;

namespace SDemo.Shell.Wpf.Panel;

/// <summary>
/// 壳面板（DA-98 / DA-100 / DA-08 / DA-09 / DA-62 / DA-66 / DA-69）：
/// 组件管理 + 设置（config Form / 权限 / 热键 / 托盘入口 / 隐私）+ 诊断。不承载聊天或业务视图。
/// 关闭面板只回托盘，不退出进程。
/// </summary>
public sealed class ShellPanelWindow : Window
{
    private readonly AppRegistry _registry;
    private readonly AppProcessManager _manager;
    private readonly AppInstaller _installer;
    private readonly WidgetController _controller;
    private readonly ShellPaths _paths;
    private readonly ShellStateStore _store;
    private readonly SystemThemeMonitor _themeMonitor;
    private readonly HotkeyService _hotkeys;
    private readonly IShellLog _log;

    private readonly StackPanel _componentRows = new();
    private readonly StackPanel _settingsRows = new();
    private readonly TextBox _diagnosticsBox = new();
    private readonly TextBlock _statusLine = new();
    private readonly Border _emptyPanel;
    private readonly TabControl _tabs;
    private ShellTheme _theme;
    private bool _allowClose;

    public ShellPanelWindow(
        AppRegistry registry,
        AppProcessManager manager,
        AppInstaller installer,
        WidgetController controller,
        ShellPaths paths,
        ShellStateStore store,
        SystemThemeMonitor themeMonitor,
        HotkeyService hotkeys,
        IShellLog? log = null)
    {
        _registry = registry;
        _manager = manager;
        _installer = installer;
        _controller = controller;
        _paths = paths;
        _store = store;
        _themeMonitor = themeMonitor;
        _hotkeys = hotkeys;
        _log = log ?? NullShellLog.Instance;
        _theme = controller.CurrentTheme;

        Title = $"SDemo DesktopAssembly {HostVersion.Product}";
        Width = 760;
        Height = 660;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        ApplyTheme();

        _emptyPanel = BuildEmptyPanel();
        _tabs = BuildLayout();

        _registry.Changed += OnRegistryChanged;
        _themeMonitor.Changed += OnThemeChanged;
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

    public void ShowPanel(bool diagnostics = false)
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
        if (diagnostics)
        {
            _tabs.SelectedIndex = 2;
        }
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    public void Refresh()
    {
        _componentRows.Children.Clear();
        _settingsRows.Children.Clear();
        var entries = _registry.Entries;
        _emptyPanel.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in entries)
        {
            _componentRows.Children.Add(BuildRow(entry));
            _settingsRows.Children.Add(BuildSettingsCard(entry));
        }

        _settingsRows.Children.Add(BuildShellSettingsCard());

        var invalid = _registry.InvalidCount;
        _statusLine.Text = invalid > 0
            ? $"组件 {entries.Count} 个 · {invalid} 个加载失败（查看诊断） · {_paths.AppsDirectory}"
            : $"组件 {entries.Count} 个 · 上限 {_registry.ComponentLimit} · {_paths.AppsDirectory}";
        _statusLine.Foreground = invalid > 0 ? _theme.DangerBrush : _theme.MutedBrush;

        if (_tabs.SelectedIndex == 2)
        {
            RefreshDiagnostics();
        }
    }

    private TabControl BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
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
            Foreground = _theme.MutedBrush,
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var tabs = new TabControl
        {
            Background = _theme.SurfaceBrush,
            BorderBrush = _theme.BorderBrush,
            BorderThickness = new Thickness(1),
        };

        var components = new TabItem { Header = "组件" };
        var componentContent = new Grid();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _componentRows,
            Padding = new Thickness(10),
        };
        componentContent.Children.Add(scroll);
        componentContent.Children.Add(_emptyPanel);
        components.Content = componentContent;

        var settings = new TabItem { Header = "设置" };
        settings.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _settingsRows,
            Padding = new Thickness(10),
        };

        var diagnostics = new TabItem { Header = "诊断" };
        diagnostics.Content = BuildDiagnosticsPanel();

        tabs.Items.Add(components);
        tabs.Items.Add(settings);
        tabs.Items.Add(diagnostics);
        tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == 2)
            {
                RefreshDiagnostics();
            }
        };

        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new WrapPanel();
        buttons.Children.Add(CreateButton("安装应用（zip）", (_, _) => InstallFromZip()));
        buttons.Children.Add(CreateButton("打开安装目录", (_, _) => OpenFolder(_paths.AppsDirectory)));
        buttons.Children.Add(CreateButton("全部启动", (_, _) => _controller.ShowAll()));
        buttons.Children.Add(CreateButton("隐藏全部", (_, _) => _controller.HideAll()));
        buttons.Children.Add(CreateButton("刷新", (_, _) => Refresh()));
        buttons.Children.Add(CreateButton("退出外壳", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
        footer.Children.Add(buttons);
        _statusLine.FontSize = 11;
        _statusLine.Margin = new Thickness(0, 8, 0, 0);
        _statusLine.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_statusLine);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        return tabs;
    }

    private Border BuildEmptyPanel()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 480,
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
            Foreground = _theme.MutedBrush,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"应用数据目录（卸载不删）：{ShellPaths.AppDataDirectoryFor("<appId>")}",
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = _theme.MutedBrush,
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
            Background = _theme.ElevatedBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Margin = new Thickness(10),
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
            : $"{StateLabel(entry.State)} · {entry.Widget.Renderer} · {entry.WidgetId} · pid {entry.Runtime?.Pid.ToString() ?? "-"} · 端口 {entry.Runtime?.Port.ToString() ?? "-"}";
        info.Children.Add(new TextBlock
        {
            Text = stateText,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = entry.LastError is not null ? _theme.DangerBrush : _theme.MutedBrush,
        });

        var hotkeyError = _controller.HotkeyError(entry.WidgetId);
        if (hotkeyError is not null)
        {
            info.Children.Add(new TextBlock
            {
                Text = $"热键：{hotkeyError}",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = _theme.DangerBrush,
            });
        }

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
        if (entry.Widget.Renderer == WidgetRendering.Native)
        {
            actions.Children.Add(CreateToggle("穿透", entry.ClickThrough, (_, _) =>
            {
                _controller.ToggleClickThrough(entry.WidgetId);
                Refresh();
            }));
        }

        actions.Children.Add(CreateButton("诊断", (_, _) =>
        {
            _tabs.SelectedIndex = 2;
            RefreshDiagnostics();
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
            Background = _theme.SurfaceBrush,
            BorderBrush = _theme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private Border BuildSettingsCard(AppEntry entry)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{entry.Widget.Name}（{entry.WidgetId}）",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });

        // 热键
        panel.Children.Add(BuildHotkeyRow(entry));

        // 托盘入口（DA-72）
        var trayCheck = new CheckBox
        {
            Content = "在托盘菜单显示该组件入口",
            IsChecked = entry.EffectiveShowInTray,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = _theme.TextBrush,
        };
        trayCheck.Click += (_, _) =>
        {
            var value = trayCheck.IsChecked == true;
            _registry.UpdateWidgetState(entry.WidgetId, state => state.ShowInTray = value);
            Refresh();
        };
        panel.Children.Add(trayCheck);

        // 权限（DA-66）
        panel.Children.Add(BuildPermissionsBlock(entry));

        // config 表单（DA-08 / DA-62 / DA-63）
        panel.Children.Add(BuildConfigForm(entry));

        return new Border
        {
            Background = _theme.SurfaceBrush,
            BorderBrush = _theme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
    }

    private UIElement BuildHotkeyRow(AppEntry entry)
    {
        var row = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = "全局热键（留空 = 不注册；格式如 Ctrl+Alt+T）",
            FontSize = 11,
            Foreground = _theme.MutedBrush,
        });

        var input = new TextBox
        {
            Text = entry.EffectiveHotkey ?? string.Empty,
            Width = 220,
            Margin = new Thickness(0, 4, 0, 0),
            Background = _theme.ElevatedBrush,
            Foreground = _theme.TextBrush,
            BorderBrush = _theme.BorderBrush,
        };

        var line = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        line.Children.Add(input);
        line.Children.Add(CreateButton("应用热键", (_, _) => ApplyHotkey(entry, input.Text)));
        line.Children.Add(CreateButton("清除", (_, _) =>
        {
            input.Text = string.Empty;
            ApplyHotkey(entry, string.Empty);
        }));

        var status = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = _theme.MutedBrush,
            Text = entry.EffectiveHotkey is null
                ? "未配置"
                : _controller.IsHotkeyRegistered(entry.WidgetId)
                    ? "已注册"
                    : _controller.HotkeyError(entry.WidgetId) ?? "未注册",
        };
        if (_controller.HotkeyError(entry.WidgetId) is not null)
        {
            status.Foreground = _theme.DangerBrush;
        }

        row.Children.Add(line);
        row.Children.Add(status);
        return row;
    }

    private UIElement BuildPermissionsBlock(AppEntry entry)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "权限",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        });

        var declared = entry.Widget.Permissions;
        if (declared is not { Count: > 0 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = "该组件未声明权限（最小权限）",
                FontSize = 11,
                Foreground = _theme.MutedBrush,
                Margin = new Thickness(0, 4, 0, 0),
            });
            return panel;
        }

        foreach (var permission in declared)
        {
            var line = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var decision = PermissionPolicy.DecisionFor(entry.WidgetState, permission);
            var known = PermissionPolicy.IsKnown(permission);
            line.Children.Add(new TextBlock
            {
                Text = $"{permission}：{PermissionDescription(permission, decision)}",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = known ? _theme.TextBrush : _theme.MutedBrush,
            });

            if (known && !PermissionPolicy.IsGranted(entry.WidgetState, permission))
            {
                line.Children.Add(CreateButton("授权", (_, _) => SetPermission(entry, permission, true)));
            }
            else if (known && decision == PermissionPolicy.Granted && permission == PermissionPolicy.Notifications)
            {
                line.Children.Add(CreateButton("撤销", (_, _) => SetPermission(entry, permission, false)));
            }

            panel.Children.Add(line);
        }

        return panel;
    }

    private UIElement BuildConfigForm(AppEntry entry)
    {
        var schema = WidgetConfigSchema.Parse(entry.Widget.Config);
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "组件设置（值存 widgets.json，不写应用数据）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        });

        if (schema.Fields.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = schema.IsSupported ? "该组件没有可调设置" : string.Join("；", schema.Issues),
                FontSize = 11,
                Foreground = _theme.MutedBrush,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            return panel;
        }

        var values = WidgetConfigSchema.ApplyDefaults(schema, entry.WidgetState.Config);
        var editors = new Dictionary<string, Func<JsonElement?>>(StringComparer.Ordinal);

        foreach (var field in schema.Fields)
        {
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = field.Title + (field.Description is null ? string.Empty : $"（{field.Description}）"),
                FontSize = 11,
                Foreground = _theme.MutedBrush,
            });

            JsonElement? current = values.TryGetValue(field.Key, out var stored) ? stored : null;
            switch (field.Kind)
            {
                case ConfigFieldKind.Boolean:
                {
                    var check = new CheckBox
                    {
                        IsChecked = current is { ValueKind: JsonValueKind.True },
                        Content = field.Title,
                        Foreground = _theme.TextBrush,
                    };
                    editors[field.Key] = () => JsonSerializer.SerializeToElement(check.IsChecked == true, ShellJson.File);
                    row.Children.Add(check);
                    break;
                }

                case ConfigFieldKind.Enum:
                {
                    var combo = new ComboBox { Width = 240, Background = _theme.ElevatedBrush };
                    foreach (var option in field.EnumValues)
                    {
                        combo.Items.Add(option);
                    }

                    combo.SelectedItem = current is { ValueKind: JsonValueKind.String } text
                        ? text.GetString()
                        : field.EnumValues.FirstOrDefault();
                    editors[field.Key] = () => JsonSerializer.SerializeToElement(combo.SelectedItem as string ?? string.Empty, ShellJson.File);
                    row.Children.Add(combo);
                    break;
                }

                default:
                {
                    var input = new TextBox
                    {
                        Text = current?.ToString() ?? string.Empty,
                        Width = 240,
                        Background = _theme.ElevatedBrush,
                        Foreground = _theme.TextBrush,
                        BorderBrush = _theme.BorderBrush,
                    };
                    var line = new WrapPanel();
                    line.Children.Add(input);

                    if (field.Kind == ConfigFieldKind.Path)
                    {
                        line.Children.Add(CreateButton("选择…", (_, _) =>
                        {
                            var dialog = new OpenFileDialog { CheckFileExists = false, Title = field.Title };
                            if (dialog.ShowDialog(this) == true)
                            {
                                input.Text = dialog.FileName;
                            }
                        }));
                    }

                    editors[field.Key] = () => BuildValue(field, input.Text);
                    row.Children.Add(line);
                    break;
                }
            }

            panel.Children.Add(row);
        }

        var save = CreateButton("保存设置", (_, _) =>
        {
            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, read) in editors)
            {
                if (read() is { } value)
                {
                    payload[key] = value;
                }
            }

            if (_controller.ApplyConfig(entry.WidgetId, payload, out var error))
            {
                _statusLine.Text = $"已保存 {entry.WidgetId} 的设置";
            }
            else
            {
                MessageBox.Show(this, error ?? "保存失败", "组件设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Refresh();
        });
        save.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(save);
        return panel;
    }

    private Border BuildShellSettingsCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "外壳设置",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });

        var privacy = new CheckBox
        {
            Content = "截屏 / 录屏时隐藏组件窗口（DA-45）",
            IsChecked = _store.Settings.Privacy.HideFromCapture,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = _theme.TextBrush,
        };
        privacy.Click += (_, _) =>
        {
            var enabled = privacy.IsChecked == true;
            _store.Settings.Privacy.HideFromCapture = enabled;
            _store.SaveSettings();
            _controller.SetHideFromCapture(enabled);
        };
        panel.Children.Add(privacy);

        panel.Children.Add(new TextBlock
        {
            Text = "壳级热键（留空 = 不注册）",
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = _theme.MutedBrush,
        });

        var toggleWidgets = new TextBox
        {
            Text = _store.Settings.Hotkeys.ToggleWidgets ?? string.Empty,
            Width = 220,
            Margin = new Thickness(0, 4, 0, 0),
            Background = _theme.ElevatedBrush,
            Foreground = _theme.TextBrush,
            BorderBrush = _theme.BorderBrush,
        };
        var toggleThrough = new TextBox
        {
            Text = _store.Settings.Hotkeys.ToggleClickThrough ?? string.Empty,
            Width = 220,
            Margin = new Thickness(0, 4, 0, 0),
            Background = _theme.ElevatedBrush,
            Foreground = _theme.TextBrush,
            BorderBrush = _theme.BorderBrush,
        };

        panel.Children.Add(LabeledRow("显示 / 隐藏全部组件", toggleWidgets));
        panel.Children.Add(LabeledRow("切换全部组件点击穿透", toggleThrough));
        panel.Children.Add(CreateButton("保存壳级热键", (_, _) =>
        {
            var widgetsHotkey = NormalizeHotkeyOrNull(toggleWidgets.Text);
            var throughHotkey = NormalizeHotkeyOrNull(toggleThrough.Text);
            if (toggleWidgets.Text.Trim().Length > 0 && widgetsHotkey is null)
            {
                MessageBox.Show(this, $"热键格式不合法：{toggleWidgets.Text}", "壳级热键", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (toggleThrough.Text.Trim().Length > 0 && throughHotkey is null)
            {
                MessageBox.Show(this, $"热键格式不合法：{toggleThrough.Text}", "壳级热键", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _store.Settings.Hotkeys.ToggleWidgets = widgetsHotkey;
            _store.Settings.Hotkeys.ToggleClickThrough = throughHotkey;
            _store.SaveSettings();
            _controller.RebindHotkeys();
            var error = _controller.HotkeyError(ShellHotkeyOwners.ToggleWidgets)
                        ?? _controller.HotkeyError(ShellHotkeyOwners.ToggleClickThrough);
            if (error is not null)
            {
                MessageBox.Show(this, $"壳级热键未能注册：{error}", "热键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _statusLine.Text = "壳级热键已保存";
            Refresh();
        }));

        return new Border
        {
            Background = _theme.SurfaceBrush,
            BorderBrush = _theme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 8, 0, 0),
            Child = panel,
        };
    }

    private Grid BuildDiagnosticsPanel()
    {
        var grid = new Grid { Margin = new Thickness(10) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(CreateButton("刷新诊断", (_, _) => RefreshDiagnostics()));
        buttons.Children.Add(CreateButton("复制诊断", (_, _) => CopyDiagnostics()));
        buttons.Children.Add(CreateButton("打开日志目录", (_, _) => OpenFolder(_paths.LogsDirectory)));
        buttons.Children.Add(CreateButton("打开安装目录", (_, _) => OpenFolder(_paths.AppsDirectory)));
        grid.Children.Add(buttons);

        _diagnosticsBox.IsReadOnly = true;
        _diagnosticsBox.AcceptsReturn = true;
        _diagnosticsBox.TextWrapping = TextWrapping.NoWrap;
        _diagnosticsBox.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
        _diagnosticsBox.FontSize = 12;
        _diagnosticsBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _diagnosticsBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _diagnosticsBox.Background = _theme.ElevatedBrush;
        _diagnosticsBox.Foreground = _theme.TextBrush;
        _diagnosticsBox.BorderBrush = _theme.BorderBrush;
        Grid.SetRow(_diagnosticsBox, 1);
        grid.Children.Add(_diagnosticsBox);
        return grid;
    }

    private void ApplyTheme()
    {
        _theme = _controller.CurrentTheme;
        Background = _theme.BackgroundBrush;
        Foreground = _theme.TextBrush;
        if (_diagnosticsBox is not null)
        {
            _diagnosticsBox.Background = _theme.ElevatedBrush;
            _diagnosticsBox.Foreground = _theme.TextBrush;
            _diagnosticsBox.BorderBrush = _theme.BorderBrush;
        }

        if (_statusLine is not null)
        {
            _statusLine.Foreground = _theme.MutedBrush;
        }

        if (_tabs is not null)
        {
            _tabs.Background = _theme.SurfaceBrush;
            _tabs.BorderBrush = _theme.BorderBrush;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ApplyTheme();
        Refresh();
    });

    private void RefreshDiagnostics()
    {
        _diagnosticsBox.Text = WidgetDiagnostics.BuildOverview(
            _registry.Entries,
            _paths,
            _log,
            WasmEnvironment.Status);
    }

    private void ApplyHotkey(AppEntry entry, string raw)
    {
        var text = raw.Trim();
        if (text.Length > 0)
        {
            if (!HotkeyGesture.TryParse(text, out var gesture))
            {
                MessageBox.Show(
                    this,
                    $"热键格式不合法：{text}\n\n示例：Ctrl+Alt+T（必须至少一个修饰键）。",
                    "热键",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            text = gesture!.ToCanonicalString();
        }

        _registry.UpdateWidgetState(entry.WidgetId, state => state.HotkeyOverride = text.Length == 0 ? null : text);
        _controller.RebindHotkeys();
        var error = _controller.HotkeyError(entry.WidgetId);
        if (error is not null)
        {
            MessageBox.Show(
                this,
                $"热键未能注册：{error}\n\n（可能被其它程序占用，换一个组合再试。）",
                "热键冲突",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Refresh();
    }

    private void SetPermission(AppEntry entry, string permission, bool granted)
    {
        _registry.UpdateWidgetState(entry.WidgetId, state => PermissionPolicy.SetDecision(state, permission, granted));
        Refresh();
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(_diagnosticsBox.Text.Length > 0 ? _diagnosticsBox.Text : _controller.BuildDiagnosticsText());
            _statusLine.Text = "诊断信息已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            _log.Warn("复制诊断信息失败。", ex);
            _statusLine.Text = $"复制失败：{ex.Message}";
        }
    }

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

    private static JsonElement? BuildValue(ConfigField field, string text)
    {
        var trimmed = text.Trim();
        if (field.Kind is ConfigFieldKind.Text or ConfigFieldKind.Path)
        {
            return JsonSerializer.SerializeToElement(trimmed, ShellJson.File);
        }

        if (field.Kind == ConfigFieldKind.Integer && long.TryParse(trimmed, out var number))
        {
            return JsonSerializer.SerializeToElement(number, ShellJson.File);
        }

        if (field.Kind == ConfigFieldKind.Number
            && double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real))
        {
            return JsonSerializer.SerializeToElement(real, ShellJson.File);
        }

        return null;
    }

    private static string? NormalizeHotkeyOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return HotkeyGesture.TryParse(raw, out var gesture) ? gesture!.ToCanonicalString() : null;
    }

    private static string PermissionDescription(string permission, string decision) => permission switch
    {
        PermissionPolicy.HttpLocalhost => $"本地回环 HTTP（默认给） · {PermissionPolicy.Describe(decision)}",
        PermissionPolicy.Notifications => $"通知 · {PermissionPolicy.Describe(decision)}",
        _ => "宿主不支持该权限（只展示）",
    };

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

    private static Button CreateButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 6),
        };
        button.Click += handler;
        return button;
    }

    private System.Windows.Controls.Primitives.ToggleButton CreateToggle(
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
            Background = _theme.ElevatedBrush,
            Foreground = _theme.TextBrush,
            BorderBrush = _theme.BorderBrush,
            BorderThickness = new Thickness(1),
        };
        toggle.Click += (sender, args) =>
        {
            handler(sender, args);
            toggle.IsChecked = isChecked;
        };
        return toggle;
    }

    private static UIElement LabeledRow(string label, TextBox input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brushes.Gray,
        });
        panel.Children.Add(input);
        return panel;
    }
}
