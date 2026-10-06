using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
// Runs in the ordinary-privilege dock, never as a child of the elevated stage.
internal static class DockAppLauncher
{
    // Verified from this computer's desktop application inventory, not the CLI executable.
    private const string CodexAppId="OpenAI.Codex_2p2nqsd0c76g0!App";
    internal static bool ValidAppId(string id) {
        return !String.IsNullOrWhiteSpace(id) && Regex.IsMatch(id,@"\A[A-Za-z0-9_.!\-]+\z") && id.IndexOf('!')>0;
    }
    internal static void Open(bool codex,Action<string> report) {
        var worker=new Thread(delegate() {
            try {
                if(codex && !ValidAppId(CodexAppId))throw new InvalidOperationException("Application identity invalid");
                string target=codex?"shell:AppsFolder\\"+CodexAppId:"shell:AppsFolder";
                Process.Start(new ProcessStartInfo("explorer.exe",target){UseShellExecute=true});
                report(codex?"Codex 已交给 Windows 打开。":"已打开 Windows 全部应用。");
            }catch(Exception ex){report("应用入口未能打开："+ex.GetType().Name);}
        });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }
}
