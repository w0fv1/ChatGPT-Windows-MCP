using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ChatGPTWindowsMcp;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.ToolStripMenuItem _status = new() { Enabled = false };
    private readonly Forms.ToolStripMenuItem _toggle = new();
    private readonly Dictionary<Color, Icon> _icons = new();

    public TrayIcon(Action show, Action toggle, Action logs, Action exit)
    {
        _menu.Items.Add("ChatGPT Windows MCP").Enabled = false;
        _menu.Items.Add(_status);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("显示主窗口", null, (_, _) => show());
        _toggle.Click += (_, _) => toggle();
        _menu.Items.Add(_toggle);
        _menu.Items.Add("打开日志", null, (_, _) => logs());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exit());
        _notify = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "ChatGPT Windows MCP",
            Icon = GetIcon(Color.Gray),
            Visible = true
        };
        _notify.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        _notify.BalloonTipClicked += (_, _) => show();
    }

    public void Update(string status, bool running, bool busy, bool healthy, bool faulted)
    {
        _status.Text = status;
        var tooltip = "ChatGPT Windows MCP · " + status;
        _notify.Text = tooltip.Length > 63 ? tooltip[..60] + "…" : tooltip;
        _notify.Icon = GetIcon(busy ? Color.RoyalBlue : faulted ? Color.Firebrick :
            running ? healthy ? Color.SeaGreen : Color.DarkOrange : Color.Gray);
        _toggle.Text = busy ? "正在处理…" : running ? "停止服务" : "启动服务";
        _toggle.Enabled = !busy;
    }

    public void Notify(string title, string message, bool error = false)
    {
        _notify.ShowBalloonTip(5000, title, message,
            error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private Icon GetIcon(Color color)
    {
        if (_icons.TryGetValue(color, out var cached)) return cached;
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(32, 42, 57));
            graphics.FillRectangle(background, 2, 3, 26, 22);
            using var outline = new Pen(Color.White, 2);
            graphics.DrawRectangle(outline, 5, 6, 20, 14);
            graphics.DrawLine(outline, 15, 21, 15, 27);
            graphics.DrawLine(outline, 10, 28, 20, 28);
            using var dot = new SolidBrush(color);
            graphics.FillEllipse(background, 19, 18, 13, 13);
            graphics.FillEllipse(dot, 21, 20, 9, 9);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            var icon = (Icon)borrowed.Clone();
            _icons.Add(color, icon);
            return icon;
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }
}
