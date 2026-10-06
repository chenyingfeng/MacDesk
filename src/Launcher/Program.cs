using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args.Length == 1 && args[0] == "/stop") { Stop(); return; }
            if (args.Length == 1 && args[0] == "/help") { Application.Run(new LauncherForm()); return; }
            if (args.Length == 0 || (args.Length == 1 && args[0] == "/admin"))
            {
                Start(args.Length == 1);
                return;
            }
            throw new ArgumentException("Supported options: /admin, /stop, /help.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            if (error.NativeErrorCode != 1223) MessageBox.Show(error.Message, "MacDesk", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "MacDesk", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    internal static void Start(bool elevated)
    {
        string dock = Path.Combine(Root, "NativeDock", "PersonalDock-v4.exe");
        string stage = Path.Combine(Root, "CampusStage", "StageManager.exe");
        if (!File.Exists(dock) || !File.Exists(stage)) throw new FileNotFoundException("Extract the complete MacDesk zip to a writable folder before starting it.");
        // Elevation is explicit and affects StageManager only. Windows displays
        // its own UAC prompt; this launcher does not bypass or automate consent.
        Process.Start(new ProcessStartInfo(stage) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(stage), Verb = elevated ? "runas" : "open" });
        Process.Start(new ProcessStartInfo(dock) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(dock) });
    }

    internal static void Stop()
    {
        string stage = Path.Combine(Root, "CampusStage");
        string dock = Path.Combine(Root, "NativeDock");
        if (Directory.Exists(stage)) File.WriteAllText(Path.Combine(stage, "stop.request"), "1");
        if (Directory.Exists(dock)) File.WriteAllText(Path.Combine(dock, "exit.marker"), Guid.NewGuid().ToString("N"));
        // The existing processes close themselves and restore taskbar/window
        // state. No process is forcibly terminated by the launcher.
    }
}

internal sealed class LauncherForm : Form
{
    internal LauncherForm()
    {
        Text = "MacDesk"; ClientSize = new Size(480, 220); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var description = new Label { Left = 20, Top = 20, Width = 440, Height = 70, Text = "Start the portable Dock and sidebar. The normal option does not request administrator access. Elevated StageManager can control administrator app windows after you confirm Windows UAC." };
        Controls.Add(description);
        AddButton("Start", 20, delegate { Program.Start(false); Close(); });
        AddButton("Start with admin Stage", 160, delegate { Program.Start(true); Close(); });
        AddButton("Stop and restore", 320, delegate { Program.Stop(); Close(); });
        Controls.Add(new Label { Left = 20, Top = 165, Width = 440, Height = 40, Text = "No installation, registry changes, autostart, or bundled personal data." });
    }
    private void AddButton(string text, int left, Action action)
    {
        var button = new Button { Text = text, Left = left, Top = 110, Width = left == 160 ? 150 : 130, Height = 38 };
        button.Click += delegate {
            try { action(); }
            catch (System.ComponentModel.Win32Exception error) { if (error.NativeErrorCode != 1223) MessageBox.Show(error.Message, "MacDesk"); }
            catch (Exception error) { MessageBox.Show(error.Message, "MacDesk"); }
        };
        Controls.Add(button);
    }
}
