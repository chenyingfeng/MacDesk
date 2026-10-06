using AsyncAwaitBestPractices;
using StageManager.Helpers;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;

namespace StageManager
{
	public partial class SceneManager : IDisposable
	{
		private readonly Desktop _desktop;
		private List<Scene> _scenes = new List<Scene>();
		private readonly object _scenesLock = new object();
		private Scene? _current;
        private readonly StageSelection<Scene> _stage;
        public bool KeepWindowsVisible {get;set;}=!PortablePreferences.Read("mac-mode") && !PortablePreferences.Read("exclusive-stage");
		private bool _suspend = false;
        private readonly SemaphoreSlim _switchGate=new(1,1);
        private readonly LatestRequestQueue<IWindow> _systemChoices;
		private Scene? _lastScene; // remembers the scene that was active before desktop view
		private IWindow? _lastFocusedWindow;
		private DateTime _lastFocusChange = DateTime.MinValue; // Track rapid focus changes

		// Windows the user minimized themselves. A minimized window is never parked —
		// OpacityWindowStrategy.Hide skips iconic windows — so nothing else records that it
		// left the stage on purpose, and the restore passes in SwitchTo and
		// RestoreMinimizedInvisibly would drag it back on the next scene switch.
		private readonly HashSet<IntPtr> _userMinimized = new HashSet<IntPtr>();

		/// <summary>
		/// When set, focus-triggered scene switches use this delegate instead of calling SwitchTo directly.
		/// MainWindow sets this to inject the transition animation.
		/// </summary>
		public Func<Scene, Task<bool>>? AnimatedSwitch { get; set; }

		public event EventHandler<SceneChangedEventArgs>? SceneChanged;
		public event EventHandler<CurrentSceneSelectionChangedEventArgs>? CurrentSceneSelectionChanged;

		// Use full-transparency instead of minimising so hidden windows keep repainting and thumbnails stay live.
		private IWindowStrategy WindowStrategy { get; } = new OpacityWindowStrategy();

		public WindowsManager WindowsManager { get; }

		private const string TeamsProcessName1 = "ms-teams.exe";
		private const string TeamsProcessName2 = "teams.exe";
		private bool _disposed = false;
		private bool _hideDesktopIcons;

		/// <summary>
		/// Determines whether the given window should stay visible across scenes and therefore must not
		/// participate in Stage Manager scene logic. Currently hard-codes an exception for the Microsoft
		/// Teams ‘Meeting compact’ floating pop-up.
		/// </summary>
		private bool IsPersistentWindow(IWindow window)
		{
			if (window == null)
				return false;

			// Quick process check – bail out early if it is definitely not Teams
			var exe = window.ProcessFileName ?? string.Empty;
			if (!string.Equals(exe, TeamsProcessName1, StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(exe, TeamsProcessName2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			// Identify the floating meeting pop-up through its title. The compact meeting view always contains
			// the words “Meeting” and “compact”. Adjust the checks here if Microsoft changes the wording.
			var title = window.Title ?? string.Empty;
			return title.IndexOf("Meeting", StringComparison.OrdinalIgnoreCase) >=0 &&
			 title.IndexOf("compact", StringComparison.OrdinalIgnoreCase) >=0;
		}

		public SceneManager(WindowsManager windowsManager, bool hideDesktopIcons = false)
		{
			WindowsManager = windowsManager ?? throw new ArgumentNullException(nameof(windowsManager));
            _stage=new StageSelection<Scene>(SceneWorkspace);
            _systemChoices = new LatestRequestQueue<IWindow>(ActivateFromSystemSelection,
                ex => { RestoreFailures++; Log.Fatal("SYSTEM-SELECTION", "Restore failed: " + ex.Message); });
			_desktop = new Desktop();
			_hideDesktopIcons = hideDesktopIcons;

			// Preserve the user's existing icon visibility when this optional feature is off.
			if (_hideDesktopIcons) {
                _desktop.EnsureIconsExist();
				_desktop.HideIcons(animate: false);
            }

			Log.Info("STARTUP", $"SceneManager constructor: hideDesktopIcons={_hideDesktopIcons}");
		}

		public async Task Start()
		{
			// Check if we're on the UI thread by verifying we have access to the dispatcher
			// This is more reliable than checking for thread ID1
			if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
				throw new NotSupportedException("Start has to be called on the main thread, otherwise events won't be fired.");

			Log.Info("STARTUP", "SceneManager starting");

			WindowsManager.WindowCreated += WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed += WindowsManager_WindowDestroyed;
			WindowsManager.DesktopShortClick += WindowsManager_DesktopShortClick;

			await WindowsManager.Start();

			Log.Info("STARTUP", "SceneManager started, WindowsManager active");
		}

        internal int RestoreFailures { get; private set; }
        internal int SystemSelections { get; private set; }
		internal void Stop()
		{
			// Unsubscribe from all WindowsManager events to prevent memory leaks
			WindowsManager.WindowCreated -= WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated -= WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed -= WindowsManager_WindowDestroyed;
			WindowsManager.DesktopShortClick -= WindowsManager_DesktopShortClick;

			// Freeze event reactions and restore parked windows before clearing tracking.
            // Quitting must return to the normal desktop instead of minimizing every other app.
            _suspend = true;
            foreach (var w in WindowsManager.Windows.ToArray())
            {
                try { WindowStrategy.Show(w); }
                catch (Exception ex) { RestoreFailures++; Log.Fatal("RESTORE", $"Restore failed: {ex.Message}"); }
            }

			WindowsManager.Stop();

			if (_hideDesktopIcons)
				_desktop.RestoreIcons();
		}

		private void WindowsManager_WindowUpdated(IWindow window, WindowUpdateType type)
		{
            // A real system choice must survive an in-flight sidebar transaction.
            // Queue it before suppression, using the same serialized restoration gate.
            if (type == WindowUpdateType.SystemSelection || type == WindowUpdateType.TaskbarSelection
                || type == WindowUpdateType.ExternalSelection) {
                _systemChoices.RequestAsync(window).SafeFireAndForget();
                return;
            }
			if (_suspend)
			{
				Log.Window("EVENT", $"SUSPENDED, ignoring {type}", window);
				return;
			}

            if(type==WindowUpdateType.MoveEnd || type==WindowUpdateType.Show)
                RefreshWindowWorkspaceAsync(window).SafeFireAndForget();
            if(type==WindowUpdateType.Show) {
                // SHOW/reconciliation only bind; they must not unpark background groups
                // or cause one-second focus/switch loops.
                if(FindSceneForWindow(window)==null) SwitchToSceneByNewWindow(window).SafeFireAndForget();
                return;
            }
			if (type == WindowUpdateType.Foreground)
			{
				// Skip rapid focus changes to prevent scene switching loops
				if (IsRapidFocusChange())
				{
					Log.Window("FOCUS", "RAPID focus change, skipping", window);
					return;
				}

				// A parked window is off-screen by our own hand, so any activation it wins is
				// one nobody asked for: minimizing hands the foreground to the next window in
				// the z-chain, and parked windows are still in that chain (Hide keeps their
				// z-order deliberately). Following that event would yank the stage to a scene
				// the user never picked — and would immediately undo the stow below.
				// Time-based guards do not work here: WinEvents are queued behind our own
				// hook callback, so they land after any flag we could raise has dropped.
				if (OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out _, out _))
				{
					Log.Window("FOCUS", "Foreground on parked window, ignoring", window);
					return;
				}

				Log.Window("FOCUS", "Foreground change", window);

				_lastFocusedWindow = window; // remember for scene restore
				SwitchToSceneByWindow(window).SafeFireAndForget();
			}
			else if (type == WindowUpdateType.MinimizeStart)
			{
				OnWindowMinimized(window);
			}
			else if (type == WindowUpdateType.MinimizeEnd)
			{
				OnWindowRestored(window).SafeFireAndForget();
			}
			// Some applications surface a previously hidden window with a simple ShowWindow
			// call that does NOT bring the window to the foreground. In that case the
			// window is visible but still carries WS_EX_TRANSPARENT from our hide logic
			// and is therefore not clickable. Treat a Show event as a signal that the
			// application wants to interact again and restore normal interactivity.
			else if (type == WindowUpdateType.Show)
			{
				Log.Window("EVENT", "Show", window);

				// Option2: Make Show event authoritative for current scene windows
				var scene = FindSceneForWindow(window);
				if (scene is not null && ReferenceEquals(scene, _current))
				{
					// If the window is minimized but just got shown, ensure it is restored
					if (window.IsMinimized)
					{
						Log.Window("EVENT", "Show: restoring minimized window in current scene", window);
						if(NativeClientWindowPolicy.UsesNativeMinimize(window))WindowRestore.RequestNativeRestore(window);
                        else window.ShowNormal();
					}

					// Force clearing opacity/mouse-through regardless of skip checks
					WindowStrategy.Show(window);
				}
				else
				{
					// Restore normal interactivity for non-active scenes;
					// WindowStrategy.Show handles its own skip-checks internally.
					WindowStrategy.Show(window);
				}

				// Only switch scenes if this is actually a focus change, not just a show event
				// This prevents scene creation for minimized windows that shouldn't create scenes
				if (window.IsFocused)
				{
					Log.Window("EVENT", "Show + focused → switching scene", window);
					// Bring Stage Manager's focus model in sync by switching to the scene
					// containing this window. This guarantees proper stacking order and
					// icon visibility handling.
					SwitchToSceneByWindow(window).SafeFireAndForget();
				}
			}
		}

		/// <summary>
		/// True when the user minimized this window and has not restored it since. Such a
		/// window stays off the stage even when its scene comes back — the same way macOS
		/// leaves a minimized window in the Dock while its group is on stage.
		/// </summary>
		private bool IsUserMinimized(IWindow window)
		{
			lock (_userMinimized)
				return _userMinimized.Contains(window.Handle);
		}

		/// <summary>
		/// Drops the user-minimized mark, for callers that un-minimize a window on the user's
		/// behalf (dragging its tile out of the sidebar, moving it between scenes). Clearing it
		/// before the ShowWindow call also stops the resulting MINIMIZEEND from being read as
		/// the user restoring the window from the taskbar.
		/// </summary>
		public void ForgetUserMinimized(IWindow window)
		{
			lock (_userMinimized)
				_userMinimized.Remove(window.Handle);
		}

		/// <summary>
		/// Keeps native minimize intact and stows the group after close/restore events settle. The current
		/// group returns to the sidebar and retains live previews for an explicit restore.
		/// </summary>
        private readonly HashSet<IntPtr> _pendingSidebarStows = new();
        internal int SidebarStows { get; private set; }
        internal int SidebarActivations { get; private set; }

        private void OnWindowMinimized(IWindow window)
        {
            if (_suspend || _disposed || IsPersistentWindow(window)
                || OpacityWindowStrategy.IsStageMinimized(window.Handle)) return;
            if (!_pendingSidebarStows.Add(window.Handle)) return;
            lock(_userMinimized) _userMinimized.Add(window.Handle);
            StowAfterMinimizeAsync(window).SafeFireAndForget();
        }

        private async Task StowAfterMinimizeAsync(IWindow window)
        {
            bool held=false;
            try {
                for(int i=0;i<16 && !window.IsMinimized;i++) await Task.Delay(50);
                await Task.Delay(160);
                while (!_disposed && WindowsManager.SystemNavigationActive) await Task.Delay(100);
                await _switchGate.WaitAsync();held=true;
                if(_disposed || !_pendingSidebarStows.Remove(window.Handle)
                    || !Win32.IsWindow(window.Handle) || !Win32.IsWindowVisible(window.Handle) || !window.IsMinimized)return;
                var scene=FindSceneForWindow(window);if(scene is null)return;
                _suspend=true;
                // Preserve native minimized state. Restoring at alpha zero forced WebView
                // composition changes and could race a close-to-tray or new shell selection.
                SidebarStows++;
                if(MacMode && scene.Windows.Count()>1 && _stage.Contains(scene))SeparateWindowCore(window);
                else StowSceneCore(scene);
                SceneChanged?.Invoke(this,new SceneChangedEventArgs(scene,window,ChangeType.Updated));
            }catch(Exception ex){Log.Fatal("STOW","Stow failed: "+ex.Message);}
            finally {
                if(held){_suspend=false;_switchGate.Release();}
                _pendingSidebarStows.Remove(window.Handle);
                // Keep the mark until a real restore or explicit sidebar/system selection.
            }
        }

        public async Task<bool> ActivateFromSidebar(Scene scene)
        {
            if(!scene.Windows.Any(w=>QqWindowPolicy.Retain(w)))
                return false;
            var presented=Presented(scene,null,_stage.Contains(scene));
            foreach(var w in presented) {
                _pendingSidebarStows.Remove(w.Handle); ForgetUserMinimized(w);
            }
            SidebarActivations++;
            var target=presented.LastOrDefault();
            if(target!=null) WindowOperationReport.Write("activate-before",target,null);
            try {
                var result=await SwitchToSerialized(scene,true,OneWindowPerApp?target:null);
                if(target!=null) WindowOperationReport.Write("activate-after",target,null);
                return result;
            } catch(Exception ex) {
                if(target!=null) WindowOperationReport.Write("activate-failed",target,ex);
                throw;
            }
        }



        private async Task ActivateFromSystemSelection(IWindow window)
        {
            if (_disposed || WindowsManager.SystemNavigationActive || !Win32.IsWindow(window.Handle)
                || !Win32.IsWindowVisible(window.Handle)) return;
            await RefreshWindowWorkspaceAsync(window);
            var scene = FindSceneForWindow(window);
            if (scene == null) return;
            foreach (var member in scene.Windows.ToArray()) {
                _pendingSidebarStows.Remove(member.Handle);
                ForgetUserMinimized(member);
            }
            _lastFocusedWindow = window;
            Log.Window("SYSTEM-SELECTION", "Restoring selected window", window);
            WindowOperationReport.Write("system-selection-before", window, null);
            try {
                if (await SwitchToSerialized(scene, true, window)) SystemSelections++;
                WindowOperationReport.Write("system-selection-after", window, null);
            } catch (Exception ex) {
                WindowOperationReport.Write("system-selection-failed", window, ex);
                throw;
            }
        }

        public Task<bool> StowCurrentScene() => SwitchTo(null);

		/// <summary>
		/// Handles the user restoring a minimized window from the taskbar. Switches to the
		/// window's scene explicitly instead of leaving it to the foreground event that
		/// follows: the window may still be parked off-screen from an earlier scene switch,
		/// and the parked-window guard in the foreground handler would (correctly) drop it.
		/// </summary>
		private async Task OnWindowRestored(IWindow window)
		{
			bool wasUserMinimized;
			lock (_userMinimized)
				wasUserMinimized = _userMinimized.Remove(window.Handle);

			// Our own restore passes call ShowNormal and echo a MINIMIZEEND straight back
			// here. Those windows were never marked, so the flag doubles as the echo guard.
			if (!wasUserMinimized || _suspend)
				return;

			Log.Window("MINIMIZE", "User restored from taskbar", window);

			var scene = FindSceneForWindow(window);
			if (scene is null)
				return;

			if (ReferenceEquals(scene, _current))
			{
				WindowStrategy.Show(window);
				return;
			}

            if(OneWindowPerApp)
                await SwitchToSerialized(scene,true,window);
            else if (AnimatedSwitch != null)
				await AnimatedSwitch(scene);
			else
				await SwitchTo(scene);
		}

		private bool IsBlankDesktopClick(IntPtr handle)
		{
			var cls = DesktopShellClassifier.GetClassName(handle);

			// Ignore taskbar / other common shells
			if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase))
				return false;

			// Helper local function to evaluate selection count on a SysListView32 window
			static bool IsListViewSelectionEmpty(IntPtr listView)
			{
				if (listView == IntPtr.Zero)
					return true;

				var sel = Win32.SendMessage(listView, Win32.LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero);
				return sel == IntPtr.Zero;
			}

			// Desktop background container windows (WorkerW/Progman): blank click only when no icon selected
			if (DesktopShellClassifier.IsDesktopBackground(handle))
			{
				var shell = Desktop.FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null);
				var listView = shell != IntPtr.Zero ? Desktop.FindWindowEx(shell, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;
				return IsListViewSelectionEmpty(listView);
			}

			// Desktop icon view (list view) – ensure no icon is selected
			if (DesktopShellClassifier.IsDesktopIconView(handle))
				return IsListViewSelectionEmpty(handle);

			return false;
		}

		// MainWindow assigns this so SceneManager can ignore desktop-toggle clicks while the
		// app-filter is active — those clicks are reserved for clearing the filter (handled in
		// MainWindow.OnMousePressed).
		public Func<bool>? IsAppFilterActive { get; set; }

		private void WindowsManager_DesktopShortClick(object? sender, IntPtr handle)
		{
			if (_suspend || WindowsManager.SystemNavigationActive || PortablePreferences.Read("disable-wallpaper-toggle"))
				return;

			if (IsAppFilterActive?.Invoke() == true)
			{
				Log.Info("DESKTOP", "Click during app-filter — desktop toggle suppressed");
				return;
			}

			// Only treat clicks on truly blank desktop areas as a toggle trigger
			if (!IsBlankDesktopClick(handle))
			{
				Log.Info("DESKTOP", "Click on desktop icon, not blank area — ignoring", handle);
				return;
			}

            ToggleDesktopAsync().SafeFireAndForget();
		}

        private void WindowsManager_WindowDestroyed(IWindow window)
        {
            if(!Application.Current.Dispatcher.CheckAccess()) {
                Application.Current.Dispatcher.BeginInvoke(new Action(()=>WindowsManager_WindowDestroyed(window)));
                return;
            }
            OpacityWindowStrategy.CleanupWindow(window.Handle);
            ForgetZOrder(window.Handle); ForgetUserMinimized(window);
            _pendingSidebarStows.Remove(window.Handle);
            var scene=FindSceneForWindow(window); if(scene==null)return;
            scene.Remove(window);
            if(scene.Windows.Any()) {
                SceneChanged?.Invoke(this,new SceneChangedEventArgs(scene,window,ChangeType.Updated));
                return;
            }
            lock(_scenesLock) _scenes.Remove(scene);
            var prior=_current;
            _stage.Remove(scene);
            if(ReferenceEquals(_current,scene)) _current=_stage.Current;
            if(ReferenceEquals(_lastScene,scene))_lastScene=null;
            SceneChanged?.Invoke(this,new SceneChangedEventArgs(scene,window,ChangeType.Removed));
            if(!ReferenceEquals(prior,_current))
                CurrentSceneSelectionChanged?.Invoke(this,new CurrentSceneSelectionChangedEventArgs(prior,_current));
            // Closing a window removes its card only; do not focus unrelated parked apps.
        }

        private void StowSceneCore(Scene scene)
        {
            var prior=_current;
            _stage.Remove(scene);
            foreach(var member in scene.Windows.ToArray())
                if(WindowRestore.SameWindow(member)&&WorkspaceEnvironment.IsCurrent(member.Handle)
                    &&!WorkspaceEnvironment.IsFullscreen(member.Handle))WindowStrategy.Hide(member);
            _current=_stage.Current;
            foreach(var s in GetScenes()) s.IsSelected=ReferenceEquals(s,_current);
            CurrentSceneSelectionChanged?.Invoke(this,new CurrentSceneSelectionChangedEventArgs(prior,_current));
        }

		public Scene? FindSceneForWindow(IWindow window) => FindSceneForWindow(window.Handle);

		public Scene? FindSceneForWindow(IntPtr handle)
		{
			lock (_scenesLock)
				return _scenes.FirstOrDefault(s => s.Windows.Any(w => w.Handle == handle));
		}

		private Scene? FindSceneForProcess(string processName)
		{
			lock (_scenesLock)
				return _scenes.FirstOrDefault(s => string.Equals(s.Key, processName, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Pulls tracked windows of the same app that belong to no scene into
		/// <paramref name="scene"/>. A window only joins a scene when it gains focus, so an
		/// app that opens several windows at once leaves every unfocused one orphaned —
		/// parked off-screen with no tile and no way of ever coming back.
		/// </summary>
		private void AdoptOrphanWindows(Scene scene)
		{
			foreach (var window in WindowsManager.Windows.ToArray())
			{
				if (IsPersistentWindow(window) || FindSceneForWindow(window) is not null)
					continue;
				if (!string.Equals(GetWindowGroupKey(window), scene.Key, StringComparison.OrdinalIgnoreCase))
					continue;

				scene.Add(window);
				Log.Scene("Adopted orphaned window of the same app", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}
		}

		private async void WindowsManager_WindowCreated(IWindow window, bool firstCreate)
		{
			SwitchToSceneByNewWindow(window).SafeFireAndForget();
		}

		private async Task SwitchToSceneByWindow(IWindow window)
		{
            QqWindowPolicy.RememberFocused(window);
			// Keep persistent windows (e.g. Teams meeting pop-ups) outside of scene logic.
			if (IsPersistentWindow(window))
			{
				Log.Window("SCENE", "Persistent window, skipping scene logic", window);
				return;
			}

			// Only create/switch scenes for windows that are actually focused, not just shown
			// This prevents scene creation for minimized windows that get Show events without focus
			if (!window.IsFocused)
			{
				Log.Window("SCENE", "Window not focused, skipping scene switch", window);
				return;
			}

			var scene = FindSceneForWindow(window);
			if (scene is null)
			{
				// Window not yet bound to any scene. Before creating a new one, check whether
				// a scene already exists for this process (race: focus event can fire before
				// SwitchToSceneByNewWindow has bound the window). If so, adopt the window into
				// the existing scene to prevent duplicate scenes for the same app.
				var key = GetWindowGroupKey(window);
				var byKey = FindSceneForProcess(key);
				if (byKey is not null)
				{
					byKey.Add(window);
					scene = byKey;
					Log.Scene("Adopted window into existing scene by process key", scene, window);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
				}
				else
				{
					scene = new Scene(key, window);
					lock (_scenesLock)
						_scenes.Add(scene);
					Log.Scene("Created new scene for window", scene, window);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
					AdoptOrphanWindows(scene);
				}
			}
			else
			{
				Log.Scene("Switching to existing scene", scene, window);
			}

			if (AnimatedSwitch != null)
				await AnimatedSwitch(scene);
			else
				await SwitchTo(scene);
		}

        private async Task SwitchToSceneByNewWindow(IWindow window)
        {
            if(_disposed || IsPersistentWindow(window) || !window.CanLayout) return;
            SceneBindings.Result binding;
            foreach(var existing in GetScenes())SceneWorkspace(existing);
            lock(_scenesLock) binding=SceneBindings.Bind(_scenes,window,WorkspaceEnvironment.WindowKey(window));
            if(binding.Added) SceneChanged?.Invoke(this,new SceneChangedEventArgs(binding.Scene,window,
                binding.Created ? ChangeType.Created : ChangeType.Updated));
            // Being unfocused must never prevent a window appearing in the sidebar.
            // Leave an unfocused new window alone; it does not get to steal focus.
            if(window.IsFocused && !_suspend) await SwitchToSerialized(binding.Scene,OneWindowPerApp,window);
        }


		/// <summary>
		/// Determines if a scene is switched back to shortly after it has been hidden.
		/// This can happen if an app activates one of it's windows after being hidde,
		/// like Microsoft Teams does if there's a small floating window for a current call.
		/// </summary>
		/// <param name="scene"></param>
		/// <returns></returns>
		/// <summary>
		/// Determines if focus changes are happening too rapidly to indicate system vs user interaction
		/// This helps prevent scene switching loops from automatic focus changes
		/// </summary>
		/// <returns></returns>
		private bool IsRapidFocusChange()
		{
			var now = DateTime.Now;
			if ((now - _lastFocusChange).TotalMilliseconds <100) // Less than100ms since last focus change
			{
				Log.Info("FOCUS", "Rapid focus change detected, filtering");
				_lastFocusChange = now;
				return true; // This is a rapid focus change
			}
			_lastFocusChange = now;
			return false;
		}

		public Task<bool> SwitchTo(Scene? scene) => SwitchToSerialized(scene,false);
        private async Task<bool> SwitchToSerialized(Scene? scene,bool forceRestore,IWindow? selectedWindow=null)
        {
            var dispatcher=Application.Current.Dispatcher;
            if(!dispatcher.CheckAccess())
                return await dispatcher.InvokeAsync(()=>SwitchToSerialized(scene,forceRestore,selectedWindow)).Task.Unwrap();
            await _switchGate.WaitAsync();
            try {
                if(_disposed) return false;
                if (WindowsManager.SystemNavigationActive) return false;
                return await SwitchToCore(scene,forceRestore,selectedWindow);
            } finally { _switchGate.Release(); }
        }
        private async Task<bool> SwitchToCore(Scene? scene,bool forceRestore,IWindow? selectedWindow=null)
        {
            if(scene!=null && (!GetScenes().Contains(scene) || !scene.Windows.Any(w=>WorkspaceEnvironment.IsCurrent(w.Handle))))return false;
            long navigationEpoch = WindowsManager.SystemNavigationEpoch;
            long inputEpoch = WindowsManager.ExplicitInputEpoch;
            IntPtr foregroundBefore = Win32.GetForegroundWindow();
			if (!forceRestore && object.Equals(scene, _current) && (scene!=null||_stage.ActiveItems.Count==0))
			{
				Log.Info("SWITCH", $"Already on scene '{scene?.Title}', skipping");
				return false;
			}

			Log.Info("SWITCH", $"SwitchTo START: '{_current?.Title}' → '{scene?.Title ?? "(desktop)"}'");

			IWindow? focusCandidate = null;

			try
			{
				_suspend = true;

                var prior=_current;
                if(scene==null && _stage.ActiveItems.Count>0) {
                    _desktopReturn=_stage.ActiveItems.ToArray();_workspaceReturn[_stage.ActiveScope]=_desktopReturn;
                }
                _stage.Select(scene,KeepWindowsVisible);
                var visibleTarget=scene==null?Array.Empty<IWindow>():Presented(scene,selectedWindow);
                var keptHandles=_stage.Items.SelectMany(s=>ReferenceEquals(s,scene)?visibleTarget:Presented(s)).Select(w=>w.Handle).ToHashSet();
                var context=scene!=null?SceneWorkspace(scene):_stage.ActiveScope;
                var otherWindows=GetSceneableWindows().Where(w=>WorkspaceSwitchPolicy.MayPark(context,
                    FindSceneForWindow(w) is Scene owner?SceneWorkspace(owner):WorkspaceEnvironment.WindowKey(w),
                    keptHandles.Contains(w.Handle),WorkspaceEnvironment.IsCurrent(w.Handle),WorkspaceEnvironment.IsFullscreen(w.Handle))).ToArray();
                _current=scene;

				Scene[] scenesSnapshot;
				lock (_scenesLock)
					scenesSnapshot = _scenes.ToArray();
				foreach (var s in scenesSnapshot)
				{
					if(SceneWorkspace(s)==context)s.IsSelected = s.Equals(scene);
				}

				// Read the outgoing stacking BEFORE anything is hidden — parking a window
				// off-screen leaves it in the z-chain, but the order is only meaningful while
				// the scene is still the one on screen.
				CaptureZOrder(otherWindows);
                using var motion=new Services.WindowMotion(otherWindows,visibleTarget,WorkspaceEnvironment.MonitorFromKey(context));

				Log.Info("SWITCH", $"Hiding {otherWindows.Length} windows");
				foreach (var o in otherWindows)
				{
					Log.Window("HIDE", "Hiding", o);
					WindowStrategy.Hide(o);
				}

				// Phase2: bring in target-scene windows.
                await motion.PlayAsync(()=>_disposed||WindowsManager.SystemNavigationActive
                    ||WindowsManager.SystemNavigationEpoch!=navigationEpoch||WindowsManager.ExplicitInputEpoch!=inputEpoch);
                if(_disposed)return false;
				if (scene is object)
				{
					Log.Frame("SWITCH", $"Show pass START, {scene.Windows.Count()} windows in target scene");
					// Bottom-most first: Show ends in BringWindowToTop, so whichever window is
					// shown last ends up on top. Feeding them in reverse depth order replays the
					// stacking the scene had when it was last on screen.
					foreach (var w in OrderBottomToTop(visibleTarget))
					{
                        // A member may close while another awaits native restore.
                        if(!WindowRestore.SameWindow(w)||!WorkspaceEnvironment.IsCurrent(w.Handle)) continue;
                        if(!Win32.IsWindowVisible(w.Handle) && !(forceRestore && QqWindowPolicy.IsLauncher(w)))continue;
                        if ((w.IsMinimized && !IsUserMinimized(w)) || !Win32.IsWindowVisible(w.Handle))
                        {
                            Log.Window("SHOW", "Restoring minimized", w);
                            await WindowRestore.RunAsync(w,forceRestore);
						}

						Log.Window("SHOW", "Showing", w);
						// Always clear any previous opacity/click-through for active scene windows
						WindowStrategy.Show(w);
					}
					// Same frame as START means the whole scene was unparked inside one compose
					// and any remaining stagger is downstream of here — the cards, or the apps
					// repainting. A later frame means the pass itself is what splits them.
					Log.Frame("SWITCH", "Show pass END");

					// Determine which window should get focus after restore – pick the last
					// focused window if it belongs to the scene and is not minimised, otherwise
					// the one that was frontmost. Focusing raises a window to the top, so taking
					// the first in list order here would undo the stacking just restored above.
                    if (selectedWindow != null && scene.Windows.Contains(selectedWindow)
                        && Win32.IsWindow(selectedWindow.Handle) && Win32.IsWindowVisible(selectedWindow.Handle) && !selectedWindow.IsMinimized)
                        focusCandidate = selectedWindow;
                    else if (_lastFocusedWindow is object && scene.Windows.Contains(_lastFocusedWindow)
                        && Win32.IsWindow(_lastFocusedWindow.Handle) && Win32.IsWindowVisible(_lastFocusedWindow.Handle) && !_lastFocusedWindow.IsMinimized)
						focusCandidate = _lastFocusedWindow;
					else
                        focusCandidate = OrderBottomToTop(visibleTarget).LastOrDefault(w => Win32.IsWindow(w.Handle) && Win32.IsWindowVisible(w.Handle) && !w.IsMinimized);

					Log.Window("SWITCH", "Focus candidate", focusCandidate ?? scene.Windows.FirstOrDefault());
				}

				await Task.Delay(100);
                if(_disposed) return false;
                CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(prior, _current));

				if (scene is null)
				{
					_lastScene = prior;
					if (_hideDesktopIcons)
					{
						Log.Info("DESKTOP", "Showing desktop icons (switched to desktop view)");
						_desktop.ShowIcons();
					}
				}
				else
				{
					_lastScene = null;
					if (_hideDesktopIcons)
					{
						Log.Info("DESKTOP", "Hiding desktop icons (switched to scene)");
						_desktop.HideIcons();
					}
				}
			}
			finally
			{
				// Focus while reaction suppression is still held; nested events must not
                // enqueue an automatic scene change over the explicit click.
                try {
                    if (!_disposed && focusCandidate is object && FocusHandoff.MayFinish(foregroundBefore,
                        Win32.GetForegroundWindow(), scene?.Windows.Select(w => w.Handle).ToArray() ?? Array.Empty<IntPtr>(),
                        inputEpoch, WindowsManager.ExplicitInputEpoch, navigationEpoch, WindowsManager.SystemNavigationEpoch,
                        WindowsManager.SystemNavigationActive) && WorkspaceEnvironment.IsCurrent(focusCandidate.Handle)) focusCandidate.Focus();
                }
                finally { _suspend=false; }

				Log.Info("SWITCH", $"SwitchTo END: now on '{_current?.Title ?? "(desktop)"}'");
			}

			return true;
		}

        private Task RestoreAsync(IWindow window)=>WindowRestore.RunAsync(window);

        internal async Task DropSceneFromSidebar(Scene scene,IWindow window,Rect targetPixels)
        {
            WindowOperationReport.Write("drop-before",window,null);
            await _switchGate.WaitAsync();
            try {
                if(_disposed || !WindowRestore.SameWindow(window))throw new InvalidOperationException("原窗口已关闭，请重新选择。");
                if(!ReferenceEquals(FindSceneForWindow(window),scene))throw new InvalidOperationException("窗口分组已改变，请重新选择缩略图。");
                var dropMonitor=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)targetPixels.X,(int)targetPixels.Y)).DeviceName;
                _stage.ActiveScope=WorkspaceEnvironment.Key(dropMonitor,WorkspaceEnvironment.CurrentDesktop);
                var activeTarget=_stage.Current;
                var mergeTarget=MacMode && activeTarget!=null && activeTarget!=scene && GetScenes().Contains(activeTarget)?activeTarget:null;
                _suspend=true;
                foreach(var member in scene.Windows.ToArray()) {
                    _pendingSidebarStows.Remove(member.Handle);ForgetUserMinimized(member);
                }
                if(window.IsMinimized || !Win32.IsWindowVisible(window.Handle))await WindowRestore.RunAsync(window,true);
                await WindowRestore.NormalizeAsync(window);
                // The app can finish restoring after the pointer is released. Read its real
                // normal size now, rather than forcing a fallback preview's 800x600 size.
                var actual=window.Location;
                var work=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)targetPixels.X,(int)targetPixels.Y)).WorkingArea;
                var cursor=new Point(targetPixels.Left+targetPixels.Width/2,targetPixels.Top+16);
                targetPixels=RightSidebarGeometry.DropBounds(new Rect(work.X,work.Y,work.Width,work.Height),cursor,
                    new Size(actual.Width>0?actual.Width:targetPixels.Width,actual.Height>0?actual.Height:targetPixels.Height));
                bool arrived=false;
                for(int attempt=0;attempt<3 && !arrived;attempt++) {
                    if(!WindowRestore.SameWindow(window) || !Win32.IsWindowVisible(window.Handle))
                        throw new InvalidOperationException("应用在拖出期间隐藏或关闭了窗口，请再试一次。");
                    if(!Win32.SetWindowPos(window.Handle,IntPtr.Zero,(int)targetPixels.X,(int)targetPixels.Y,0,0,
                        Win32.SetWindowPosFlags.IgnoreResize | Win32.SetWindowPosFlags.IgnoreZOrder |
                        Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.AsynchronousWindowPosition))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"应用拒绝移动窗口。");
                    for(int i=0;i<16;i++) {
                        await Task.Delay(25);
                        var current=window.Location;
                        if(WindowRestore.SameWindow(window) && Win32.IsWindowVisible(window.Handle) && !window.IsMinimized
                            && Math.Abs(current.X-targetPixels.X)<=12 && Math.Abs(current.Y-targetPixels.Y)<=12) {arrived=true;break;}
                    }
                }
                if(!arrived)throw new TimeoutException("应用尚未接受拖动位置，请稍后再试。");
                OpacityWindowStrategy.ForgetOriginalPosition(window.Handle);
                if(WorkspaceEnvironment.MonitorFromKey(SceneWorkspace(scene))!=dropMonitor) {
                    if(scene.Windows.Count()==1)scene.WorkspaceKey=WorkspaceEnvironment.WindowKey(window);
                    else {
                        scene.Remove(window);var relocated=new Scene(GetWindowGroupKey(window),window){WorkspaceKey=WorkspaceEnvironment.WindowKey(window)};
                        lock(_scenesLock)_scenes.Add(relocated);
                        SceneChanged?.Invoke(this,new SceneChangedEventArgs(scene,window,ChangeType.Updated));
                        SceneChanged?.Invoke(this,new SceneChangedEventArgs(relocated,window,ChangeType.Created));scene=relocated;
                    }
                }
                if(mergeTarget!=null) {
                    MergeDraggedWindow(scene,window,mergeTarget);
                    await SwitchToCore(mergeTarget,true,window);
                }else await SwitchToCore(scene,true,window);
                if(!WindowRestore.SameWindow(window) || !Win32.IsWindowVisible(window.Handle) || window.IsMinimized)
                    throw new InvalidOperationException("窗口未完成恢复，请重新选择。");
                SidebarActivations++;WindowOperationReport.Write("drop-after",window,null);
            }catch(Exception ex) {
                RestoreFailures++;WindowOperationReport.Write("drop-failed",window,ex);throw;
            }finally {_suspend=false;_switchGate.Release();}
        }

        internal async Task ArrangeStageAsync(Rect workPixels)
        {
            await _switchGate.WaitAsync();
            try {
                if(_disposed)return;
                _suspend=true;
                var windows=_stage.ActiveItems.SelectMany(s=>s.Windows).Where(w=>
                    Win32.IsWindow(w.Handle) && WorkspaceEnvironment.IsCurrent(w.Handle)
                    && !WorkspaceEnvironment.IsFullscreen(w.Handle) && !IsUserMinimized(w)).ToArray();
                var positions=RightSidebarGeometry.TileBounds(workPixels,windows.Length);
                for(int i=0;i<windows.Length;i++) {
                    var w=windows[i];
                    if(!Win32.IsWindow(w.Handle))continue;
                    if(w.IsMinimized || !Win32.IsWindowVisible(w.Handle))await RestoreAsync(w);
                    if(w.IsMaximized)await WindowRestore.NormalizeAsync(w);
                    if(!Win32.IsWindow(w.Handle))continue;
                    var r=positions[i];
                    OpacityWindowStrategy.ForgetOriginalPosition(w.Handle);
                    if(!Win32.SetWindowPos(w.Handle,IntPtr.Zero,(int)r.X,(int)r.Y,(int)r.Width,(int)r.Height,
                        Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate |
                        Win32.SetWindowPosFlags.AsynchronousWindowPosition))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"应用拒绝调整窗口大小。");
                }
                await Task.Delay(150);
                foreach(var s in _stage.Items)
                    if(s.Windows.FirstOrDefault() is IWindow first)
                        SceneChanged?.Invoke(this,new SceneChangedEventArgs(s,first,ChangeType.Updated));
            } finally {_suspend=false;_switchGate.Release();}
        }



        public async Task MoveWindow(Scene sourceScene,IWindow window,Scene targetScene)
        {
            await _switchGate.WaitAsync();
            try {
                if(_disposed || sourceScene==targetScene || !GetScenes().Contains(targetScene)
                    || !ReferenceEquals(FindSceneForWindow(window),sourceScene) || !QqWindowPolicy.Retain(window))return;
                _suspend=true;
                MergeDraggedWindow(sourceScene,window,targetScene);
                if(_stage.Contains(targetScene)) {
                    ForgetUserMinimized(window);
                    await SwitchToCore(targetScene,true,window);
                }else WindowStrategy.Hide(window);
            }finally {_suspend=false;_switchGate.Release();}
        }

		public async Task MoveWindow(IntPtr handle, Scene targetScene)
		{
			var source = FindSceneForWindow(handle);

			if (source is null || source.Equals(targetScene))
				return;

			var window = source.Windows.First(w => w.Handle == handle);
			await MoveWindow(source, window, targetScene);
		}

		public async Task PopWindowFrom(Scene sourceScene)
		{
			if (sourceScene is null || _current is null || sourceScene.Equals(_current))
				return;

			var window = sourceScene.Windows.LastOrDefault();

			if (window is object)
			{
				Log.Window("DRAG", $"Pulling window from '{sourceScene.Title}' into '{_current.Title}'", window);
				await MoveWindow(sourceScene, window, _current).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Removes a window from its current scene and creates a new scene for it in the sidebar.
		/// The window is hidden (alpha→0). Returns the new scene, or null if the operation was skipped.
		/// </summary>

		private IEnumerable<IWindow> GetSceneableWindows() => WindowsManager.Windows.Where(w => !IsPersistentWindow(w) && w.CanLayout && !string.IsNullOrEmpty(w.ProcessFileName) && !string.IsNullOrEmpty(w.Title));

		public IEnumerable<Scene> GetScenes()
		{
			lock (_scenesLock)
			{
				if (_scenes.Count == 0)
				{
					var restorePath = App.RestoreScenesPath;
					if (restorePath != null)
					{
						_scenes = RestoreScenesFromSnapshot(restorePath) ?? new List<Scene>();
						UpdateService.CleanupStagingFolder();
					}

					if (_scenes.Count == 0)
					{
						_scenes = GetSceneableWindows()
							// Include all windows during initial startup (including minimized ones) for automatic scene population
                            .Where(w => QqWindowPolicy.Retain(w))
							.GroupBy(GetWindowGroupKey)
							.Select(group => new Scene(group.Key, group.ToArray()))
							.ToList();
					}

                    if(KeepWindowsVisible) foreach(var s in _scenes)
                        if(s.Windows.Any(w=>Win32.IsWindowVisible(w.Handle) && !w.IsMinimized)) _stage.Seed(s);
					Log.Info("STARTUP", $"Initial scenes: {_scenes.Count}");
					foreach (var scene in _scenes)
						Log.Scene("Initial scene", scene);
				}

				return _scenes.ToList();
			}
		}

		public SceneSnapshot.Snapshot CreateSnapshot()
		{
			Scene[] snap;
			Scene? currentScene;
			lock (_scenesLock)
			{
				snap = _scenes?.ToArray() ?? Array.Empty<Scene>();
				currentScene = _current;
			}

			var activeHandles = currentScene?.Windows.Select(w => (long)w.Handle).ToArray() ?? Array.Empty<long>();
			var sceneEntries = snap.Select(s => new SceneSnapshot.SceneEntry(
				s.Key,
				s.Windows.Select(w => (long)w.Handle).ToArray()
			)).ToArray();

			return new SceneSnapshot.Snapshot(activeHandles, sceneEntries);
		}

		private List<Scene>? RestoreScenesFromSnapshot(string path)
		{
			var snapshot = SceneSnapshot.Load(path);
			if (snapshot is null)
			{
				Log.Info("STARTUP", "Scene snapshot missing or corrupt, falling back to default grouping");
				return null;
			}

			var allWindows = GetSceneableWindows()
				.Where(w => Win32.IsWindowVisible(w.Handle) || w.IsMinimized)
				.ToDictionary(w => (long)w.Handle);

			var claimed = new HashSet<long>();
			var scenes = new List<Scene>();

			foreach (var entry in snapshot.Scenes)
			{
				var validWindows = entry.Handles
					.Where(h => Win32.IsWindow((IntPtr)h) && allWindows.ContainsKey(h))
					.Select(h => { claimed.Add(h); return allWindows[h]; })
					.ToArray();

				if (validWindows.Length > 0)
				{
					scenes.Add(new Scene(entry.Key, validWindows));
					Log.Info("STARTUP", $"Restored scene '{entry.Key}' with {validWindows.Length}/{entry.Handles.Length} windows");
				}
			}

			// Unclaimed windows get default PID grouping
			var unclaimed = allWindows
				.Where(kv => !claimed.Contains(kv.Key))
				.Select(kv => kv.Value);

			var defaultScenes = unclaimed
				.GroupBy(GetWindowGroupKey)
				.Select(g => new Scene(g.Key, g.ToArray()));
			scenes.AddRange(defaultScenes);

			Log.Info("STARTUP", $"Scene restore complete: {scenes.Count} scenes ({claimed.Count} windows restored)");
			return scenes.Count > 0 ? scenes : null;
		}

		public bool IsCurrentScene(Scene? scene) => scene!=null && _stage.Contains(scene);

		/// <summary>
		/// Re-stows a window off-screen via the active strategy. Used to return a
		/// dragged tray window to its parked state when a sidebar drag is cancelled,
		/// so it stays hidden on stage while the live tile keeps capturing it.
		/// </summary>
		#region Z-order preservation
		// Depth of a window the last time its scene was on screen: 0 = frontmost, larger =
		// further back. Without this a switch rebuilt the stack from Scene.Windows list order,
		// so two overlapping windows swapped which one was on top every time.
		private readonly Dictionary<IntPtr, int> _zDepth = new();
		private readonly object _zDepthLock = new();

		/// <summary>
		/// Records how deep each of <paramref name="windows"/> currently sits in the desktop
		/// z-chain. Walks the chain once front-to-back and stops as soon as every window of
		/// interest has been placed, so the cost is bounded by the windows above the last one.
		/// </summary>
		private void CaptureZOrder(IReadOnlyCollection<IWindow> windows)
		{
			if (windows.Count == 0) return;

			var wanted = new HashSet<IntPtr>(windows.Select(w => w.Handle));
			var found = new Dictionary<IntPtr, int>(wanted.Count);
			var depth = 0;

			for (var h = Win32.GetTopWindow(IntPtr.Zero);
				h != IntPtr.Zero && found.Count < wanted.Count;
				h = Win32.GetWindow(h, Win32.GW.GW_HWNDNEXT))
			{
				if (wanted.Contains(h))
					found[h] = depth++;
			}

			if (found.Count == 0) return;

			lock (_zDepthLock)
			{
				foreach (var (handle, d) in found)
					_zDepth[handle] = d;
			}
		}

		/// <summary>
		/// The scene's windows ordered back-to-front, so a caller that brings each one to the
		/// top in turn ends with the captured stacking. Windows with no captured depth (never
		/// seen on screen — a brand new window, or one added while the scene was hidden) sort
		/// to the back, behind everything whose position is actually known.
		/// </summary>
		private IEnumerable<IWindow> OrderBottomToTop(IEnumerable<IWindow> windows)
		{
			lock (_zDepthLock)
			{
				return windows
					.OrderByDescending(w => _zDepth.TryGetValue(w.Handle, out var d) ? d : int.MaxValue)
					.ToArray();
			}
		}

		private void ForgetZOrder(IntPtr handle)
		{
			lock (_zDepthLock)
				_zDepth.Remove(handle);
		}
		#endregion

		public void ParkWindow(IWindow window) => WindowStrategy.Hide(window);

		/// <summary>
		/// Restores a parked window to its saved on-stage rect (and full alpha). Counterpart
		/// to <see cref="ParkWindow"/>; used to cancel a stage→tray drag.
		/// </summary>
		public void RestoreWindow(IWindow window) => WindowStrategy.Show(window);

		public bool IsDesktopView => _stage.ActiveItems.Count==0;

        public IEnumerable<IWindow> GetCurrentWindows() => _stage.ActiveItems.SelectMany(s=>s.Windows).ToArray();

		/// <summary>
		/// Instantly hides all windows in the current scene (alpha→0) without doing a full SwitchTo.
		/// Used by the transition animation so the outgoing placeholder covers the real window.
		/// </summary>
		public void HideCurrentSceneWindows()
		{
			if (_current == null)
			{
				Log.Info("SWITCH", "Pre-hide: no current scene, nothing to hide");
				return;
			}
			var windows = _current.Windows.ToArray();
			Log.Info("SWITCH", $"Pre-hiding {windows.Length} windows in '{_current.Title}' for animation");

			// The animated path parks the outgoing scene here, before SwitchTo runs, so this is
			// the last moment its stacking is readable. Capture it or coming back to this scene
			// finds no depths recorded and falls back to list order.
			CaptureZOrder(windows);

			foreach (var w in windows)
				WindowStrategy.Hide(w);
		}


		/// <summary>
		/// Restores minimized windows in a scene at alpha=0 so they have real screen positions
		/// but are invisible. Prevents the Windows taskbar restore animation on first switch.
		/// </summary>
        public void RestoreMinimizedInvisibly(Scene scene)
        {
            // Retained compatibility entry point: restoration now occurs only on selection.
        }

		/// <summary>
		/// Shows desktop icons immediately (used when setting is disabled)
		/// </summary>
		public void ShowDesktopIcons()
		{
			_desktop.ShowIcons();
		}

		/// <summary>
		/// Hides desktop icons immediately (used when setting is enabled)
		/// </summary>
		public void HideDesktopIcons()
		{
			_desktop.HideIcons();
		}

		// Group windows by **process id** instead of the process name so that every
		// newly-launched program (i.e. a new process, even if it shares the same
		// executable name with another instance) gets its **own** scene.
		//
		// This fulfils the requirement that launching a new program should ALWAYS
		// create a separate scene.
		private string GetWindowGroupKey(IWindow window) => window.ProcessId.ToString()+"@"+WorkspaceEnvironment.WindowKey(window);

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					// Already handled by Stop() method which should be called explicitly
					// But ensure cleanup in case Dispose is called directly
					_disposed = true;
                    Stop();
				}
				_disposed = true;
			}
		}
	}
}
