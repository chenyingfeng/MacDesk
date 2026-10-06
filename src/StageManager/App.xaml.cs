using AsyncAwaitBestPractices;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using StageManager.Services;

namespace StageManager
{
	public partial class App : Application
	{
		private System.Threading.Mutex? _single;
        private TaskbarGuard? _taskbarGuard;
        internal StageUiHealth? UiHealth {get;private set;}
        internal static string? RestoreScenesPath { get; private set; }

		protected override async void OnStartup(StartupEventArgs e)
		{
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"CampusStageLegionDiagnostic",StringComparison.OrdinalIgnoreCase)) {
                WindowStateDiagnostics.Run("LegionZone",Path.Combine(AppContext.BaseDirectory,"legion-window-state.json"));Shutdown();return;
            }
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"CampusStageWindowDiagnostic",StringComparison.OrdinalIgnoreCase)) {
                WindowStateDiagnostics.Run("QQ",Path.Combine(AppContext.BaseDirectory,"qq-window-state.json"));Shutdown();return;
            }
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"CampusStageInputFixture",StringComparison.OrdinalIgnoreCase)) {
                MainWindow=new SidebarInputFixture(Path.Combine(AppContext.BaseDirectory,"desktop-input-check.txt"));MainWindow.Show();return;
            }
			if(e.Args.Length>0 && e.Args[0]=="--taskbar-guard") {Shutdown(TaskbarGuard.Run(e.Args));return;}
            if(e.Args.Length==1 && e.Args[0]=="--restore-taskbar") {File.WriteAllText(TaskbarGuard.RestoreFile,"1");Shutdown();return;}
            if(e.Args.Length==1 && e.Args[0]=="--groups") {File.WriteAllText(Path.Combine(PortablePreferences.Root,"groups.request"),Guid.NewGuid().ToString("N"));Shutdown();return;}
            if (Array.IndexOf(e.Args, "--stop") >= 0) {
                File.WriteAllText(PortablePreferences.StopFile, "1"); Shutdown(); return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--selftest") {
                try { File.WriteAllText(e.Args[1], await PortableChecks.RunAsync()); }
                catch(Exception ex) {File.WriteAllText(e.Args[1],"FAIL: "+ex.Message);Shutdown(1);return;}
                Shutdown(); return;
            }
            if(e.Args.Length==2 && e.Args[0]=="--groups-preview") {
                try {new MainWindow().RenderGroupsPreview(e.Args[1]);}
                catch(Exception ex){File.WriteAllText(e.Args[1]+".error",ex.ToString());Shutdown(1);return;}
                Shutdown();return;
            }
            if(e.Args.Length==2 && e.Args[0]=="--restore-preview") {
                try {new MainWindow().RenderRestorePreview(e.Args[1]);}
                catch(Exception ex){File.WriteAllText(e.Args[1]+".error",ex.ToString());Shutdown(1);return;}
                Shutdown();return;
            }
            if(e.Args.Length==2 && e.Args[0]=="--dwmtest") {
                try {File.WriteAllText(e.Args[1],DwmSmokeChecks.Run());}
                catch(Exception ex){File.WriteAllText(e.Args[1],"FAIL: "+ex.GetType().Name+": "+ex.Message);Shutdown(1);return;}
                Shutdown();return;
            }
            if(e.Args.Length==2 && e.Args[0]=="--input-fixture") {
                MainWindow=new SidebarInputFixture(e.Args[1]);MainWindow.Show();return;
            }
            _single = new System.Threading.Mutex(true, "Local\\CampusStage.Portable.v1", out var created);
            if (!created) { _single.Dispose(); _single=null; Shutdown(); return; }
            if(File.Exists(PortablePreferences.StopFile)) File.Delete(PortablePreferences.StopFile);
            var oldGroups=Path.Combine(PortablePreferences.Root,"groups.request");if(File.Exists(oldGroups))File.Delete(oldGroups);
            base.OnStartup(e);
            ShutdownMode=ShutdownMode.OnMainWindowClose;
            UiHealth=new StageUiHealth();

			// Before anything that logs a frame number, so the count covers the whole run.
			FrameClock.Start();
            DwmSmokeChecks.WriteStartupReport();

			RestoreScenesPath = ParseRestoreScenesArg(e.Args);
			// Custom builds never download or apply upstream binaries automatically.

			Services.ThemeManager.ApplyTheme();
			Services.ThemeManager.StartListening();

			// Keep the Windows capture indicator; do not request borderless capture automatically.

			// Attempt normal restoration before terminating after a UI-thread failure.
			DispatcherUnhandledException += (s, args) =>
			{
				Log.Fatal("CRASH", $"UI thread: {args.Exception}");
                args.Handled=true;
                try { MainWindow?.Close(); } finally { Shutdown(1); }
			};

			AppDomain.CurrentDomain.UnhandledException += (s, args) =>
			{
				Log.Fatal("CRASH", $"Unhandled: {args.ExceptionObject}");
			};

			TaskScheduler.UnobservedTaskException += (s, args) =>
			{
				Log.Fatal("CRASH", $"Unobserved task: {args.Exception}");
			};
            SimpleProfile.Apply();
            MainWindow=new MainWindow();
            MainWindow.Show();
            _taskbarGuard=await TaskbarGuard.StartAsync();
		}

		protected override void OnExit(ExitEventArgs e)
		{
			UiHealth?.Dispose();
			WorkspaceEnvironment.ShutdownQueries();
			_taskbarGuard?.Dispose();
            Services.ThemeManager.StopListening();
            _single?.Dispose();
			base.OnExit(e);
		}

		private static string? ParseRestoreScenesArg(string[] args)
		{
			var index = Array.IndexOf(args, "--restore-scenes");
			if (index < 0 || index + 1 >= args.Length)
				return null;

			var path = Path.GetFullPath(args[index + 1]);
			var expectedDir = Path.GetFullPath(UpdateService.StagingFolder);

			if (!path.StartsWith(expectedDir, StringComparison.OrdinalIgnoreCase))
				return null;

			return path;
		}
	}
}
