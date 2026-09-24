using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AgentDeck;

/// <summary>Notification-area icon: open, start-with-Windows toggle, quit.</summary>
sealed class TrayIcon : IDisposable
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "AgentDeck";

    readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action open, Action quit)
    {
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartsWithWindows };
        startup.CheckedChanged += (_, _) => SetStartWithWindows(startup.Checked);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open AgentDeck", null, (_, _) => open()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit AgentDeck", null, (_, _) => quit()));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        _icon = new Forms.NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application,
            Text = "AgentDeck",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
    }

    /// <summary>Per-user login startup via HKCU\...\Run; no admin rights needed.</summary>
    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue(RunValue, false);
        Log.Info($"Start with Windows: {enabled}");
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
