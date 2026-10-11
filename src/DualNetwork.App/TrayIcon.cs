using System.Windows;
using DualNetwork.Core.Planning;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DualNetwork.App;

/// <summary>Notification-area icon: shows the mode at a glance, opens the window, and shows notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _baseIcon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private Drawing.Icon? _current;
    private Severity? _shown;

    public TrayIcon(Action open, Action toggle, Action exit)
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/DualNetwork.ico"))!.Stream;
        _baseIcon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);

        _toggle = new Forms.ToolStripMenuItem("Turn off Dual Network Mode", null, (_, _) => toggle());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open Dual Network", null, (_, _) => open()) { Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold) });
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit (restores the original configuration)", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon { Icon = _baseIcon, Text = "Dual Network", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => open();
    }

    /// <summary>Updates tooltip, status dot and menu text.</summary>
    public void Update(string title, string detail, Severity severity, bool active)
    {
        var text = $"Dual Network — {title}\n{detail}";
        _icon.Text = text.Length > 127 ? text[..124] + "…" : text; // NotifyIcon limit
        _toggle.Text = active ? "Turn off Dual Network Mode" : "Turn on Dual Network Mode";
        if (_shown == severity) return;
        _shown = severity;
        var old = _current;
        _current = WithDot(severity);
        _icon.Icon = _current;
        old?.Dispose();
    }

    public void Notify(string title, string text, Severity severity) =>
        _icon.ShowBalloonTip(6000, title, text, severity switch
        {
            Severity.Problem => Forms.ToolTipIcon.Error,
            Severity.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info,
        });

    private Drawing.Icon WithDot(Severity severity)
    {
        var size = Forms.SystemInformation.SmallIconSize;
        using var bmp = new Drawing.Bitmap(size.Width, size.Height);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawIcon(_baseIcon, new Drawing.Rectangle(0, 0, size.Width, size.Height));
            var color = severity switch
            {
                Severity.Good => Drawing.Color.FromArgb(22, 163, 74),
                Severity.Warning => Drawing.Color.FromArgb(217, 119, 6),
                Severity.Problem => Drawing.Color.FromArgb(220, 38, 38),
                _ => Drawing.Color.FromArgb(148, 163, 184),
            };
            var d = size.Width * 7 / 16;
            var rect = new Drawing.Rectangle(size.Width - d - 1, size.Height - d - 1, d, d);
            using var brush = new Drawing.SolidBrush(color);
            g.FillEllipse(Drawing.Brushes.White, Drawing.Rectangle.Inflate(rect, 1, 1));
            g.FillEllipse(brush, rect);
        }
        var handle = bmp.GetHicon();
        using var temp = Drawing.Icon.FromHandle(handle);
        var icon = (Drawing.Icon)temp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
        _baseIcon.Dispose();
    }
}
