using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using SDemo.Hub;

namespace SDemo.Hub.App;

/// <summary>托盘常驻：关闭主窗只隐藏，退出必须走托盘菜单。</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly MainWindow _window;
    private readonly WidgetHostService _widgets;
    private readonly Action<string> _navigate;
    private readonly Action _exit;
    private readonly IHubLog _log;
    private bool _disposed;

    public TrayService(
        MainWindow window,
        WidgetHostService widgets,
        Action<string> navigate,
        Action exit,
        IHubLog log)
    {
        _window = window;
        _widgets = widgets;
        _navigate = navigate;
        _exit = exit;
        _log = log;

        _menu = new ContextMenuStrip();
        _menu.Opening += OnMenuOpening;

        _icon = new NotifyIcon
        {
            Text = "SDemo Hub",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _log.Info("托盘图标已创建");
    }

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e) => RebuildMenu();

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("打开 SDemo Hub", null, (_, _) => ShowWindow());
        _menu.Items.Add(new ToolStripSeparator());

        var windows = _widgets.Windows;
        var showAll = new ToolStripMenuItem("显示全部组件", null, (_, _) => _widgets.ShowAll())
        {
            Enabled = windows.Count > 0,
        };
        var hideAll = new ToolStripMenuItem("隐藏全部组件", null, (_, _) => _widgets.HideAll())
        {
            Enabled = windows.Count > 0,
        };
        _menu.Items.Add(showAll);
        _menu.Items.Add(hideAll);

        var submenu = new ToolStripMenuItem("组件");
        if (windows.Count == 0)
        {
            submenu.DropDownItems.Add(new ToolStripMenuItem("暂无桌面组件") { Enabled = false });
        }
        else
        {
            foreach (var info in windows)
            {
                var item = new ToolStripMenuItem(info.Title)
                {
                    Checked = info.Visible,
                    CheckOnClick = false,
                };
                item.Click += (_, _) => _widgets.ToggleFromHotkey(info.Key);
                submenu.DropDownItems.Add(item);

                var clickThrough = new ToolStripMenuItem("点击穿透")
                {
                    Checked = _widgets.ClickThrough(info.AppId, info.WidgetId),
                };
                clickThrough.Click += (_, _) =>
                    _widgets.SetClickThrough(
                        info.AppId,
                        info.WidgetId,
                        !_widgets.ClickThrough(info.AppId, info.WidgetId));
                submenu.DropDownItems.Add(clickThrough);
            }
        }

        _menu.Items.Add(submenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("设置", null, (_, _) =>
        {
            ShowWindow();
            _navigate("settings");
        });
        _menu.Items.Add("诊断", null, (_, _) =>
        {
            ShowWindow();
            _navigate("diagnostics");
        });
        _menu.Items.Add("打开公共目录", null, (_, _) => OpenDirectory());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出外壳", null, (_, _) => _exit());
    }

    private void OpenDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _window.AppsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Warn("打开公共目录失败", ex);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // 回退到系统图标。
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _menu.Opening -= OnMenuOpening;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
