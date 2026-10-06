using System;
using AsyncAwaitBestPractices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using StageManager.Helpers;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Native
{
	public delegate void WindowDelegate(IWindow window);
	public delegate void WindowCreateDelegate(IWindow window, bool firstCreate);
	public delegate void WindowUpdateDelegate(IWindow window, WindowUpdateType type);

	public class WindowsManager : IWindowsManager
	{
		private volatile bool _active;
        private System.Windows.Threading.DispatcherTimer? _reconcileTimer;
        private System.Windows.Threading.DispatcherTimer? _systemNavigationTimer;
        private readonly Services.SystemSelectionPolicy _systemSelection = new();
        private IntPtr _classifiedShellHandle;
        private bool _classifiedAsSelector;
        public event EventHandler<bool>? SystemNavigationChanged;
        public bool SystemNavigationActive => _systemSelection.Active || IsSystemSelector(Win32.GetForegroundWindow());
        public long SystemNavigationEpoch => _systemSelection.Epoch;
        private long _explicitInputEpoch;
        public long ExplicitInputEpoch => Interlocked.Read(ref _explicitInputEpoch);
        private readonly Services.BrowserActivationIntent _browserIntent = new();
        private long _browserIntentEpoch;
        private DateTime _externalMarkerTime;
        private string ExternalMarkerPath => System.IO.Path.Combine(Services.PortablePreferences.Root, "external-activation.request");
        private void ReadExternalIntent()
        {
            try {
                if (!System.IO.File.Exists(ExternalMarkerPath)) return;
                var stamp = System.IO.File.GetLastWriteTimeUtc(ExternalMarkerPath);
                if (stamp == _externalMarkerTime) return;
                var marker = System.IO.File.ReadAllText(ExternalMarkerPath);
                var before = _browserIntent.AcceptedRequests;
                _browserIntent.Receive(marker, DateTime.UtcNow.Ticks);
                if (_browserIntent.AcceptedRequests != before) _browserIntentEpoch = Interlocked.Increment(ref _explicitInputEpoch);
                _externalMarkerTime = stamp;
            } catch { /* A partially written marker is retried on the next tick. */ }
        }
        public bool IsCompanionWindow(IntPtr h)
        {
            try {
                var root = GetAncestor(h, 2);
                Win32.GetWindowThreadProcessId(root, out var pid);
                return pid != 0 && Services.PortablePreferences.IsExcludedProcess(Process.GetProcessById((int)pid).ProcessName);
            } catch { return false; }
        }

        private bool IsSystemSelector(IntPtr h)
        {
            // The class cache describes identity, not current visibility. Taskbar/Task
            // View hosts often remain the foreground HWND briefly after being hidden.
            if (h == IntPtr.Zero || !Win32.IsWindowVisible(h) || Services.WorkspaceEnvironment.IsCloaked(h)) return false;
            if (h == _classifiedShellHandle) return _classifiedAsSelector;
            _classifiedShellHandle = h;
            _classifiedAsSelector = false;
            try {
                Win32.GetWindowThreadProcessId(h, out var pid);
                if (pid != 0)
                    _classifiedAsSelector = Services.SystemSelectionPolicy.IsSelector(
                        Process.GetProcessById((int)pid).ProcessName, DesktopShellClassifier.GetClassName(h));
            } catch { /* Unknown/inaccessible windows remain unmanaged. */ }
            return _classifiedAsSelector;
        }

        private void FollowSystemForeground(IntPtr h, bool emitOrdinary)
        {
            if (!_active) return;
            ReadExternalIntent();
            if (_browserIntentEpoch != ExplicitInputEpoch) _browserIntent.Cancel();
            bool selector = IsSystemSelector(h);
            bool wasActive = _systemSelection.Active;
            if (!selector && emitOrdinary && !_windows.ContainsKey(h)) RegisterWindow(h);
            bool selection = _systemSelection.Observe(selector, _windows.ContainsKey(h), Environment.TickCount64);
            if (wasActive != _systemSelection.Active)
                SystemNavigationChanged?.Invoke(this, _systemSelection.Active);
            if (selector) { _browserIntent.Cancel(); return; } // Never move or group the shell's selector surface.
            if (selection) { _browserIntent.Cancel(); UpdateWindow(h, WindowUpdateType.SystemSelection); }
            else if (_windows.TryGetValue(h, out var chosen) && !chosen.IsMinimized
                && _browserIntent.TryConsume(chosen.ProcessName, true, DateTime.UtcNow.Ticks))
                UpdateWindow(h, WindowUpdateType.ExternalSelection);
            else if (emitOrdinary && _windows.TryGetValue(h, out var taskbarPick) && !taskbarPick.IsMinimized && ConsumeTaskbarActivation())
                UpdateWindow(h, WindowUpdateType.TaskbarSelection);
            else if (emitOrdinary) UpdateWindow(h, WindowUpdateType.Foreground);
            else if (_browserIntent.HasPending) {
                // ShellExecute can reuse a browser without producing FOREGROUND. Restore only
                // the queried default browser after a short handoff, never an unrelated app.
                for (var candidate = Win32.GetTopWindow(IntPtr.Zero); candidate != IntPtr.Zero;
                    candidate = Win32.GetWindow(candidate, Win32.GW.GW_HWNDNEXT)) {
                    if (_windows.TryGetValue(candidate, out var browser) && Win32.IsWindowVisible(candidate)
                        && _browserIntent.TryConsumeDefault(browser.ProcessName, true, DateTime.UtcNow.Ticks)) {
                        UpdateWindow(candidate, WindowUpdateType.ExternalSelection);
                        break;
                    }
                }
            }
        }
		private IDictionary<IntPtr, WindowsWindow> _windows;
		private WinEventDelegate _hookDelegate;

		private WindowsWindow? _mouseMoveWindow;
		private readonly object _mouseMoveLock = new object();
		private Win32.HookProc _mouseHook = null!;
		private readonly HashSet<IntPtr> _startupMinimizedHandles = new();
		private readonly List<IntPtr> _winEventHooks = new();
		private IntPtr _mouseHookHandle;

		private IntPtr _currentProcessWindowHandle;
		private int _currentProcessId;
		private long _lastLeftButtonDown;
        private long _lastTaskbarClick;
        private IntPtr _taskbarForegroundBeforeClick;
        internal bool ConsumeTaskbarActivation() {
            long stamp=Interlocked.Exchange(ref _lastTaskbarClick,0);
            return stamp!=0 && Environment.TickCount64-stamp<1500;
        }
        private static bool IsTaskbarTarget(IntPtr h) {
            var original=h;
            for(int i=0;i<8 && h!=IntPtr.Zero;i++,h=Win32.GetWindow(h,Win32.GW.GW_OWNER)) {
                var cls=DesktopShellClassifier.GetClassName(h);
                if(cls=="Shell_TrayWnd" || cls=="Shell_SecondaryTrayWnd")return true;
            }
            // Child controls are rooted in the taskbar, rather than owned by it.
            var root=GetAncestor(original,2);
            var name=DesktopShellClassifier.GetClassName(root);
            return name=="Shell_TrayWnd" || name=="Shell_SecondaryTrayWnd";
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr h,uint flags);
        private bool IsDiscoverable(IntPtr h) => !IsOwnWindow(h) && !Services.QqWindowPolicy.IsLoginSurface(h) &&
            (Win32Helper.IsAppWindow(h) || Services.QqWindowPolicy.Discover(h));
        private async Task FollowTaskbarClickAsync()
        {
            // Clicking an already-foreground parked HWND may emit no new FOREGROUND.
            // Reconcile the explicit click once, after the shell has processed it.
            long request = Interlocked.Read(ref _lastTaskbarClick);
            for (int i = 0; i < 12; i++) {
                await Task.Delay(100);
                if (!_active || request == 0 || Interlocked.Read(ref _lastTaskbarClick) != request) return;
                var h = Win32.GetForegroundWindow();
                if (_windows.ContainsKey(h) && !Win32.IsIconic(h)
                    && (h != _taskbarForegroundBeforeClick || StageManager.Strategies.OpacityWindowStrategy.TryGetOriginalPosition(h, out _, out _))) {
                    if (ConsumeTaskbarActivation()) UpdateWindow(h, WindowUpdateType.TaskbarSelection);
                    return;
                }
            }
        }
		// Double-click handling to suppress scene toggle when user double-clicks a desktop item
		private readonly int _doubleClickTime; // system double-click time in ms
		private bool _desktopClickPending;
		private long _desktopClickTime;
		private IntPtr _desktopClickHandle;
		private readonly object _desktopClickLock = new object();
		private long _lastDragEnd = 0L;
		/// <summary>
		/// Raised after a completed < 250 ms left-button click when the desktop (WorkerW/Progman) is the foreground window.
		/// The <see cref="IntPtr"/> argument is the handle of the desktop window that had focus.
		/// </summary>
		public event EventHandler<IntPtr>? DesktopShortClick;

#if DEBUG
		// Set this to true while debugging to dump detailed information about how each window is
		// evaluated for scene eligibility. The output is written via Debug.WriteLine.
		private const bool DEBUG_WINDOW_FILTER = true;

		private static string FormatStyleFlags(Win32.WS style)
		{
			var flags = new List<string>();
			if (style.HasFlag(Win32.WS.WS_SYSMENU)) flags.Add("SYSMENU");
			if (style.HasFlag(Win32.WS.WS_MINIMIZEBOX)) flags.Add("MINBOX");
			if (style.HasFlag(Win32.WS.WS_MAXIMIZEBOX)) flags.Add("MAXBOX");
			if (style.HasFlag(Win32.WS.WS_CAPTION)) flags.Add("CAPTION");
			if (style.HasFlag(Win32.WS.WS_THICKFRAME)) flags.Add("THICKFRAME");
			return string.Join("|", flags);
		}
#endif
		/// <summary>
		/// Notifies when a new window handle was created by the manager
		/// </summary>
		public event WindowCreateDelegate? WindowCreated;
		/// <summary>
		/// Notifies when a handled window was removed by the manager
		/// </summary>
		public event WindowDelegate? WindowDestroyed;
		/// <summary>
		/// Notifies when a handled window was updated by the manager
		/// This is used internally by the workspace manager to apply the update to the window
		/// </summary>
		public event WindowUpdateDelegate? WindowUpdated;

		public IEnumerable<IWindow> Windows => _windows.Values;

		public WindowsManager()
		{
			_windows = new Dictionary<IntPtr, WindowsWindow>();
			_hookDelegate = new WinEventDelegate(WindowHook);

			_doubleClickTime = (int)Win32.GetDoubleClickTime();
		}

		public Task Start()
		{
			_active = true;
            try {
                if (System.IO.File.Exists(ExternalMarkerPath)) {
                    _browserIntent.Prime(System.IO.File.ReadAllText(ExternalMarkerPath));
                    _externalMarkerTime = System.IO.File.GetLastWriteTimeUtc(ExternalMarkerPath);
                }
            } catch { }
			Log.Info("STARTUP", "WindowsManager starting, registering hooks...");

			var currentProcess = Process.GetCurrentProcess();
			_currentProcessId = currentProcess.Id;
			_currentProcessWindowHandle = currentProcess.MainWindowHandle;

			// Enumerate + un-cloak startup-minimized windows BEFORE registering hooks so the
			// forced SW_SHOWNOACTIVATE doesn't echo EVENT_SYSTEM_MINIMIZEEND back into our pipeline.
			Win32.EnumWindows((handle, param) =>
			{
				if (IsDiscoverable(handle))
				{
					// Keep startup-minimized windows minimized until an explicit sidebar click.
					if(Win32.IsWindowVisible(handle)) RescueParkedWindow(handle);
					RegisterWindow(handle, false);
				}
				return true;
			}, IntPtr.Zero);

			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_CREATE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED, Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _hookDelegate, 0, 0, 0));

			_mouseHook = MouseHook;

			var thread = new Thread(() =>
			{
				try
				{
					_mouseHookHandle = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseHook, currentProcess.MainModule!.BaseAddress, 0);
					Application.Run();
				}
				catch (Exception ex)
				{
					Log.Fatal("HOOK", $"Hook thread crashed: {ex}");
				}
			});

			thread.Name = "WindowsManager";
			thread.IsBackground = true;
			thread.Start();

			_reconcileTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
            _reconcileTimer.Tick+=(_,_)=>ReconcileWindows();
            _reconcileTimer.Start();
            _systemNavigationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _systemNavigationTimer.Tick += (_, _) => FollowSystemForeground(Win32.GetForegroundWindow(), false);
            _systemNavigationTimer.Start();
            Log.Info("STARTUP", $"WindowsManager started, tracking {_windows.Count} windows");
			return Task.CompletedTask;
		}

		public void Stop()
		{
			_active = false;
            _reconcileTimer?.Stop();
            _systemNavigationTimer?.Stop();

			foreach (var hook in _winEventHooks)
			{
				if (hook != IntPtr.Zero)
				{
					try { Win32.UnhookWinEvent(hook); } catch { /* best-effort during shutdown */ }
				}
			}
			_winEventHooks.Clear();

			if (_mouseHookHandle != IntPtr.Zero)
			{
				try { Win32.UnhookWindowsHookEx(_mouseHookHandle); } catch { /* best-effort during shutdown */ }
				_mouseHookHandle = IntPtr.Zero;
			}

			Application.Exit();

			// Restore startup-minimized windows to their original minimized state so the user
			// doesn't find them stuck cloaked at alpha=0 after the app exits.
			foreach (var hwnd in _startupMinimizedHandles)
			{
				try
				{
					var ex = Win32.GetWindowExStyleLongPtr(hwnd);
					Win32.SetWindowStyleExLongPtr(hwnd, ex & ~Win32.WS_EX.WS_EX_TRANSPARENT);
					Win32Helper.SetAlpha(hwnd, 255);
					// Leave no trace on the window: the layered style survives this process,
					// and a Chromium window that keeps it renders blank from here on.
					Win32Helper.ClearLayered(hwnd);
					Win32.ShowWindow(hwnd, Win32.SW.SW_SHOWMINNOACTIVE);
				}
				catch { /* best-effort during shutdown */ }
			}
			_startupMinimizedHandles.Clear();

			// Clean up window event subscriptions to prevent memory leaks
			foreach (var window in _windows.Values)
			{
				window?.ClearEvents();
			}

			// Clear collections to release references
			_windows.Clear();
		}

		// Where a rescued window lands, inset from the work area's top-left, and how far each
		// further one cascades so a run that parked several doesn't stack them all on one spot.
		private const int RescueInsetPx = 48;
		private const int RescueCascadePx = 32;
		private int _rescuedCount;

		/// <summary>
		/// Brings a window back on screen if a previous run died while it was parked off-screen.
		/// <para>
		/// OpacityWindowStrategy hides windows by moving them past the bottom-right of the
		/// virtual screen, and keeps the way home only in a static dictionary — so a crash, or
		/// anything else that skips Stop, takes the sole record of where they belonged with it.
		/// The window survives with a working taskbar button that cannot bring it anywhere
		/// visible, which reads as the app refusing to open.
		/// </para>
		/// <para>
		/// The original rect is gone, so this restores reachability, not layout: the size is
		/// kept and the window is dropped near the work area's top-left.
		/// </para>
		/// </summary>
		private void RescueParkedWindow(IntPtr hwnd)
		{
			// A minimized window reports a placeholder rect near (-32000,-32000), which would
			// read as off-screen. UncloakStartupMinimized has already restored those by now.
			if (Win32.IsIconic(hwnd))
				return;

			var rect = new Win32.Rect();
			if (!Win32.GetWindowRect(hwnd, ref rect))
				return;

			// Same reachability test the drag uses (DragDropManager.IsOnScreen): the park point
			// is past both the right and bottom edges of the virtual screen.
			var virtualScreen = SystemInformation.VirtualScreen;
			if (rect.Left < virtualScreen.Right && rect.Top < virtualScreen.Bottom)
				return;

			var work = Screen.PrimaryScreen?.WorkingArea ?? virtualScreen;
			var offset = RescueInsetPx + _rescuedCount++ * RescueCascadePx;

			Win32.SetWindowPos(hwnd, IntPtr.Zero,
				work.Left + offset, work.Top + offset, 0, 0,
				Win32.SetWindowPosFlags.IgnoreResize |
				Win32.SetWindowPosFlags.IgnoreZOrder |
				Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.AsynchronousWindowPosition);

			// Moving it back is not enough on its own. A window parked by a run that also went
			// through UncloakStartupMinimized carries alpha 0 and WS_EX_TRANSPARENT, which only
			// OpacityWindowStrategy.Show ever undoes — so without this the rescue would deliver
			// an invisible, click-through window to the middle of the desktop.
			var ex = Win32.GetWindowExStyleLongPtr(hwnd);
			Win32.SetWindowStyleExLongPtr(hwnd, ex & ~Win32.WS_EX.WS_EX_TRANSPARENT);
			Win32Helper.SetAlpha(hwnd, 255);
			// A window a previous run left layered renders blank in Chromium apps, so the
			// rescue has to undo that style too, not just the position and the alpha.
			Win32Helper.ClearLayered(hwnd);

			Log.Info("STARTUP", $"Rescued window 0x{hwnd.ToInt64():X} parked off-screen at ({rect.Left},{rect.Top}) by a previous run");
		}

		/// <summary>
		/// True for a window RegisterWindow drops before it reaches the candidacy filter.
		/// Checked up front because the two calls that run before RegisterWindow change the
		/// window — one un-minimizes it at alpha 0, the other moves it — and a window that is
		/// never going to be tracked is one that never gets shown either, so it would be left
		/// restored and invisible with nothing to put it back.
		/// </summary>
		private bool IsOwnWindow(IntPtr handle)
		{
			if (handle == _currentProcessWindowHandle)
				return true;

			Win32.GetWindowThreadProcessId(handle, out var processId);
			if (processId == 0 || (int)processId == _currentProcessId) return true;
            try { return Services.PortablePreferences.IsExcludedProcess(Process.GetProcessById((int)processId).ProcessName); }
            catch { return true; } // Inaccessible processes are left alone.
		}

		private void UncloakStartupMinimized(IntPtr hwnd)
		{
			if (!Win32.IsIconic(hwnd))
				return;

			try
			{
				// Cloak BEFORE the show call so the window emerges already invisible — no flash,
				// no focus steal, no z-order disruption.
				Win32Helper.SetAlpha(hwnd, 0);
				var ex = Win32.GetWindowExStyleLongPtr(hwnd);
				Win32.SetWindowStyleExLongPtr(hwnd, ex | Win32.WS_EX.WS_EX_TRANSPARENT);

				// Restore without activation. DWM now composites the window so its thumbnail goes live.
				Win32.ShowWindow(hwnd, Win32.SW.SW_SHOWNOACTIVATE);

				_startupMinimizedHandles.Add(hwnd);
				Log.Info("STARTUP", $"Uncloaked minimized window 0x{hwnd.ToInt64():X} for live preview");
			}
			catch (Exception ex)
			{
				Log.Fatal("STARTUP", $"Failed to uncloak minimized window 0x{hwnd.ToInt64():X}: {ex.Message}");
			}
		}

		public IWindowsDeferPosHandle DeferWindowsPos(int count)
		{
			var info = Win32.BeginDeferWindowPos(count);
			return new WindowsDeferPosHandle(info);
		}

		private IntPtr MouseHook(int nCode, UIntPtr wParam, IntPtr lParam)
		{
			if (nCode == 0)
			{
				var msg = (uint)wParam;

				if (msg == Win32.WM_LBUTTONDOWN)
				{
                    // Any newer explicit click invalidates an older operation's delayed focus.
                    Interlocked.Increment(ref _explicitInputEpoch);
					// If we already have a desktop click pending and another click starts within the
					// double-click interval, treat it as a double-click and cancel the pending action.
					lock (_desktopClickLock)
					{
						if (_desktopClickPending && (Environment.TickCount64 - _desktopClickTime) <= _doubleClickTime)
						{
							_desktopClickPending = false; // cancel pending single click
						}
					}

                    Win32.GetCursorPos(out var clickPoint);
                    if(IsTaskbarTarget(Win32.WindowFromPoint(new System.Drawing.Point(clickPoint.X,clickPoint.Y)))) {
                        _taskbarForegroundBeforeClick = Win32.GetForegroundWindow();
                        Interlocked.Exchange(ref _lastTaskbarClick,Environment.TickCount64);
                    }
                    else Interlocked.Exchange(ref _lastTaskbarClick, 0);
					_lastLeftButtonDown = Environment.TickCount64;
				}
				else if (msg == Win32.WM_LBUTTONUP)
				{
                    if(Interlocked.Read(ref _lastTaskbarClick)!=0)
					System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(()=>
                        FollowTaskbarClickAsync().SafeFireAndForget()));
					HandleWindowMoveEnd();

					// Detect click on desktop surface using window under cursor
					Win32.GetCursorPos(out var cursorPt);
					var windowUnderCursor = Win32.WindowFromPoint(new System.Drawing.Point(cursorPt.X, cursorPt.Y));
					if (windowUnderCursor != IntPtr.Zero)
					{
						if (DesktopShellClassifier.IsDesktopShell(windowUnderCursor))
						{
							// Suppress false desktop clicks from drag-drop mouse-up landing on desktop behind sidebar
							if ((Environment.TickCount64 - _lastDragEnd) < 300)
							{ /* drag just ended — skip desktop click */ }
							else lock (_desktopClickLock)
							{
								_desktopClickPending = true;
								_desktopClickTime = Environment.TickCount64;
								_desktopClickHandle = windowUnderCursor;
							}

							Task.Run(async () =>
							{
								await Task.Delay(_doubleClickTime);
								lock (_desktopClickLock)
								{
									if (_desktopClickPending && (Environment.TickCount64 - _desktopClickTime) >= _doubleClickTime)
									{
										DesktopShortClick?.Invoke(this, _desktopClickHandle);
									}
									_desktopClickPending = false;
								}
							});
						}
					}
				}
			}

			return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
		}

		private void WindowHook(IntPtr hWinEventHook, Win32.EVENT_CONSTANTS eventType, IntPtr hwnd, Win32.OBJID idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
		{
			if (!_active)
				return;

			// Foreground provenance includes shell surfaces that candidacy/OBJID filters exclude.
            if (eventType == Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND) {
                FollowSystemForeground(hwnd, true);
                return;
            }
            if (EventWindowIsValid(idChild, idObject, hwnd))
			{
				switch (eventType)
				{
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_CREATE:
                    case Win32.EVENT_CONSTANTS.EVENT_OBJECT_SHOW:
                        if(_windows.ContainsKey(hwnd)) UpdateWindow(hwnd,WindowUpdateType.Show);
                        else RegisterWindow(hwnd);
						break;
                    case Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE:
                        // Our off-screen parking leaves WS_VISIBLE intact. A real HIDE
                        // means the app closed to tray and must no longer be restored.
                        if(!Win32.IsWindowVisible(hwnd) && !Services.QqWindowPolicy.Discover(hwnd)) UnregisterWindow(hwnd);
                        break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY:
						UnregisterWindow(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED:
						UpdateWindow(hwnd, WindowUpdateType.Hide);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED:
						UpdateWindow(hwnd, WindowUpdateType.Show);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART:
                        // A taskbar click that minimizes must not be reinterpreted as a restore.
                        Interlocked.Exchange(ref _lastTaskbarClick,0);
						UpdateWindow(hwnd, WindowUpdateType.MinimizeStart);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND:
						UpdateWindow(hwnd, WindowUpdateType.MinimizeEnd);
						break;
					
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART:
						StartWindowMove(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND:
						EndWindowMove(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE:
						WindowMove(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE:
						if (_windows.TryGetValue(hwnd, out var compact) &&
							string.Equals(compact.ProcessFileName, "ms-teams.exe", StringComparison.OrdinalIgnoreCase) &&
							compact.Title.StartsWith("Meeting compact view", StringComparison.OrdinalIgnoreCase))
						{
							var ex = Win32.GetWindowExStyleLongPtr(hwnd);
							Win32.SetWindowStyleExLongPtr(hwnd, ex & ~Win32.WS_EX.WS_EX_TRANSPARENT);
							Win32Helper.SetAlpha(hwnd, 255);
							// Dropped from tracking for good, so nothing else will ever undo the
							// layered style for this window — do it here or not at all.
							Win32Helper.ClearLayered(hwnd);
							UnregisterWindow(hwnd);
						}
						break;
				}
			}
		}

		private bool EventWindowIsValid(int idChild, Win32.OBJID idObject, IntPtr hwnd)
		{
			return idChild == Win32.CHILDID_SELF && idObject == Win32.OBJID.OBJID_WINDOW && hwnd != IntPtr.Zero;
		}

		/// <summary>
		/// Nudge (and if oversized, shrink) a window so its whole rect fits inside
		/// the work area of the monitor it is nearest to. Used on first registration
		/// of a runtime-created window so it can never appear partly/fully off-screen.
		/// WorkingArea and SetWindowPos share the same physical-pixel space.
		/// </summary>
		private static void EnsureWindowOnScreen(WindowsWindow window)
		{
			var handle = window.Handle;
			// Leave minimized/maximized windows to the OS; their rects aren't a
			// normal restorable position.
			if (Win32.IsIconic(handle) || Win32.IsZoomed(handle))
				return;

			Win32.Rect rect = new Win32.Rect();
			if (!Win32.GetWindowRect(handle, ref rect))
				return;

			var wa = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
			int w = Math.Min(rect.Width, wa.Width);
			int h = Math.Min(rect.Height, wa.Height);
			int x = Math.Max(wa.Left, Math.Min(rect.Left, wa.Right - w));
			int y = Math.Max(wa.Top, Math.Min(rect.Top, wa.Bottom - h));

			if (x == rect.Left && y == rect.Top && w == rect.Width && h == rect.Height)
				return;

			Log.Window("ONSCREEN", $"Clamp ({rect.Left},{rect.Top},{rect.Width}x{rect.Height})→({x},{y},{w}x{h})", window);
			Win32.SetWindowPos(handle, IntPtr.Zero, x, y, w, h,
				Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.AsynchronousWindowPosition);
		}

		private void RegisterWindow(IntPtr handle, bool emitEvent = true)
		{
			if (!_active)
				return;

			if (IsOwnWindow(handle) || IsSystemSelector(handle))
				return;

			if (!_windows.ContainsKey(handle))
			{
				var window = new WindowsWindow(handle);

				if (window.ProcessId < 0 || window.ProcessId == _currentProcessId
                    || !Services.WindowIntegrity.CanControl(window.ProcessId))
					return;

#if DEBUG
				if (DEBUG_WINDOW_FILTER)
				{
					var preStyle = Win32.GetWindowStyleLongPtr(handle);
					Debug.WriteLine($"[WindowFilter] PRE     0x{((long)preStyle):X8} [{FormatStyleFlags(preStyle)}] {window}");
				}
#endif
				bool candidate = window.IsCandidate() || Services.QqWindowPolicy.IsLauncher(window);

#if DEBUG
				if (DEBUG_WINDOW_FILTER)
				{
					Debug.WriteLine($"[WindowFilter] {(candidate ? "ACCEPT" : "REJECT")} {window}");
				}
#endif

				if (candidate)
				{
					_windows[handle] = window;
					Log.Window("TRACK", "Registered", window);

					if (emitEvent)
					{
						// A freshly-launched window keeps whatever rect the app
						// restored — which can be fully off-screen (apps like Chrome
						// persist their last position, and we park hidden windows
						// off-screen, so the saved position is off-screen). Pull it
						// back so all four corners sit inside the work area before
						// the scene shows it.
						if(Win32.IsWindowVisible(handle)) EnsureWindowOnScreen(window);
						HandleWindowAdd(window, true);
					}
				}
			}
		}

        private void ReconcileWindows()
        {
            if(!_active) return;
            // A window may acquire its title/style only after SHOW. Polling closes that
            // race, without restoring, resizing or focusing unrelated applications.
            foreach(var h in _windows.Keys.ToArray()) if(!Services.QqWindowPolicy.Retain(_windows[h])) UnregisterWindow(h);
            Win32.EnumWindows((h,_)=> {
                if(IsDiscoverable(h)) {
                    if(!_windows.ContainsKey(h)) RegisterWindow(h);
                    else WindowUpdated?.Invoke(_windows[h],WindowUpdateType.Show);
                }
                return true;
            },IntPtr.Zero);
        }

		private void UnregisterWindow(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				Log.Window("TRACK", "Unregistered", window);
				_windows.Remove(handle);
				HandleWindowRemove(window);
			}
		}

		private void UpdateWindow(IntPtr handle, WindowUpdateType type)
		{
			if (!_active)
				return;

			if (type == WindowUpdateType.Show && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				WindowUpdated?.Invoke(window, type);
			}
			else if (type == WindowUpdateType.Show)
			{
				RegisterWindow(handle);
			}
			else if (type == WindowUpdateType.Hide && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				if (!Win32.IsWindowVisible(handle) && !Services.QqWindowPolicy.Discover(handle))
				{
					UnregisterWindow(handle);
				}
				else
				{
					WindowUpdated?.Invoke(window, type);
				}
			}
			else if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				WindowUpdated?.Invoke(window, type);
			}
		}

		private void StartWindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				window.StoreLastLocation();

				HandleWindowMoveStart(window);
				WindowUpdated?.Invoke(window, WindowUpdateType.MoveStart);
			}
		}

		private void EndWindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];

				HandleWindowMoveEnd();
				WindowUpdated?.Invoke(window, WindowUpdateType.MoveEnd);
			}
		}

		private void WindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_mouseMoveWindow != null && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				if (_mouseMoveWindow == window)
					WindowUpdated?.Invoke(window, WindowUpdateType.Move);
			}
		}

		private void HandleWindowMoveStart(WindowsWindow window)
		{
			if (!_active)
				return;

			if (_mouseMoveWindow != null)
				_mouseMoveWindow.IsMouseMoving = false;

			_mouseMoveWindow = window;
			window.IsMouseMoving = true;
		}

		/// <summary>
		/// Marks the button-up now being processed as the end of a drag, so it is not also
		/// read as a click on the desktop behind the sidebar.
		/// <para>
		/// Clearing <c>_desktopClickPending</c> matters as much as stamping the timestamp:
		/// MouseHook queues the pending click on WM_LBUTTONUP and only fires it a
		/// double-click interval later, and that delayed check never re-reads
		/// <c>_lastDragEnd</c>. A caller reacting to the same button-up from a different hook
		/// can therefore arrive after the click is already queued — the timestamp alone would
		/// come too late to stop it.
		/// </para>
		/// </summary>
		public void SuppressNextDesktopClick()
		{
			_lastDragEnd = Environment.TickCount64;
			lock (_desktopClickLock)
				_desktopClickPending = false;
		}

		private void HandleWindowMoveEnd()
		{
			if (!_active)
				return;

			lock (_mouseMoveLock)
			{
				if (_mouseMoveWindow != null)
				{
					var window = _mouseMoveWindow;
					_mouseMoveWindow = null;
					_lastDragEnd = Environment.TickCount64;

					window.IsMouseMoving = false;
				}
			}
		}

		private void HandleWindowAdd(IWindow window, bool firstCreate)
		{
			if (!_active)
				return;

			WindowCreated?.Invoke(window, firstCreate);
		}

		private void HandleWindowRemove(IWindow window)
		{
			if (!_active)
				return;

			WindowDestroyed?.Invoke(window);
		}
	}
}
