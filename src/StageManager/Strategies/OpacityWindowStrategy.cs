using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;

namespace StageManager.Strategies
{
	/// <summary>
	/// Hides windows by moving them off-screen (out of the visible desktop bounds) so DWM keeps
	/// compositing them and Windows.Graphics.Capture can still produce live frames for the sidebar.
	/// Off-screen positioning is required because WGC captures DWM-composited output (post-alpha) —
	/// the previous alpha=0 trick is invisible to the compositor and would yield transparent frames.
	/// </summary>
	internal class OpacityWindowStrategy : IWindowStrategy
	{
		// Saved on-screen position so Show() can restore the parked window.
		private static readonly Dictionary<IntPtr, (int X, int Y)> _originalPositions = new();

		// Atomic state management
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, System.Threading.SemaphoreSlim> _windowLocks = new();
		private static readonly object _globalLock = new object();
        // Known messenger renderers can lose input after off-screen parking. Keep these windows
        // native-minimized instead, and distinguish our requests from user minimize.
        private static readonly HashSet<IntPtr> _nativeStageMinimized = new();
        internal static bool IsStageMinimized(IntPtr handle)
        { lock(_globalLock) return _nativeStageMinimized.Contains(handle); }

		/// <summary>
		/// Returns the saved on-screen position for a window currently parked off-screen by Hide,
		/// or false if the window has no saved position. Animator uses this to compute the
		/// *intended* incoming-window rect instead of reading the live (off-screen) Bounds.
		/// </summary>
		public static bool TryGetOriginalPosition(IntPtr hWnd, out int x, out int y)
		{
			lock (_globalLock)
			{
				if (_originalPositions.TryGetValue(hWnd, out var pos))
				{
					x = pos.X; y = pos.Y;
					return true;
				}
			}
			x = 0; y = 0;
			return false;
		}

        internal static void SetRestorePosition(IntPtr handle,int x,int y)
        {
            lock(_globalLock) _originalPositions[handle]=(x,y);
        }

		/// <summary>
		/// Discards the saved position of a window that was repositioned while parked. Hide keeps
		/// the first saved position so a second Hide cannot overwrite it with the off-screen point,
		/// which also means a direct SetWindowPos behind the strategy's back (the sidebar drag moves
		/// the real window to the cursor) leaves a stale entry that Show would restore. Callers that
		/// move a parked window on purpose call this so the next Hide re-reads the live rect.
		/// </summary>
		public static void ForgetOriginalPosition(IntPtr hWnd)
		{
			lock (_globalLock)
			{
				_originalPositions.Remove(hWnd);
			}
		}

		/// <summary>
		/// Cleans up all per-window state (locks, saved positions) when a window is destroyed.
		/// </summary>
        public static void CleanupWindow(IntPtr hWnd)
        {
            // Never Wait/Dispose a semaphore in a reentrant WinEvent callback.
            _windowLocks.TryRemove(hWnd,out _);
            lock(_globalLock) { _originalPositions.Remove(hWnd); _nativeStageMinimized.Remove(hWnd); }
        }



		private static (int X, int Y) GetOffScreenPoint()
		{
			// Past the bottom-right of the virtual screen (all monitors combined).
			// Must use PHYSICAL pixels: SetWindowPos takes pixels, but WPF's
			// SystemParameters.VirtualScreen is in DIPs — under >100% scale that DIP
			// value lands back INSIDE the panel (window shows at the bottom-right corner
			// instead of parking). WinForms SystemInformation.VirtualScreen is in the same
			// physical-pixel space as SetWindowPos. +100 keeps a margin off the real edge.
			var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
			return (vs.Right + 100, vs.Bottom + 100);
		}

		private static bool ShouldSkipTransparencyForWindow(IntPtr hWnd)
		{
			if (Win32.IsIconic(hWnd))
				return true;

			if (!Win32.IsWindowVisible(hWnd))
				return true;

			// Zero is a valid extended style for custom main windows (e.g. WebView).
            if(!Win32.IsWindow(hWnd)) return true;

			return false;
		}

		public void Show(IWindow window)
		{
			var hWnd = window.Handle;
            if(!Services.WindowIntegrity.CanControl(window.ProcessId))return;
            if(IsStageMinimized(hWnd)) {
                // Normal exit restores only minimizations owned by Stage Manager.
                // A messenger closed to its tray stays hidden; do not wake it here.
                if(Services.WindowRestore.SameWindow(window) && Win32.IsWindowVisible(hWnd))
                    Services.WindowRestore.RequestNativeRestore(window);
                lock(_globalLock) _nativeStageMinimized.Remove(hWnd);
                if(window.IsMinimized || !Win32.IsWindowVisible(hWnd))return;
            }

			if (ShouldSkipTransparencyForWindow(hWnd))
			{
				Log.Window("OPACITY", "Show SKIPPED (invalid/minimized)", window);

				if (Win32.IsIconic(hWnd))
				{
					// Leave native minimize unchanged.
				}
				return;
			}

			var lockSem = _windowLocks.GetOrAdd(hWnd, _ => new System.Threading.SemaphoreSlim(1, 1));

			// Every call below crosses into the owning app's process, and the two that move
			// the window block until its UI thread answers. Each step is stamped so a scene
			// whose windows do not arrive together says which window stalled and on what.
			var tEnter = Stopwatch.GetTimestamp();

			if(!lockSem.Wait(0)) return;
			var tLock = Stopwatch.GetTimestamp();
			try
			{
				if (ShouldSkipTransparencyForWindow(hWnd))
				{
					if (Win32.IsIconic(hWnd))
					{
						// Leave native minimize unchanged.
					}
					return;
				}

				(int X,int Y) position=default; bool restore;
                lock(_globalLock) restore=_originalPositions.TryGetValue(hWnd,out position);
                if(restore) {
                    if(!Win32.SetWindowPos(hWnd,IntPtr.Zero,position.X,position.Y,0,0,
                        Win32.SetWindowPosFlags.IgnoreResize | Win32.SetWindowPosFlags.DoNotActivate |
                        Win32.SetWindowPosFlags.AsynchronousWindowPosition))
                        throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),"应用拒绝恢复窗口位置。");
                    lock(_globalLock) _originalPositions.Remove(hWnd);
                }
				var tMove = Stopwatch.GetTimestamp();

                // Ordinary parking/restoration never changes another app's alpha or styles.
                var tStyle=Stopwatch.GetTimestamp();var tAlpha=tStyle;

				window.BringToTop();
				var tRaise = Stopwatch.GetTimestamp();

				// Read the rect back instead of trusting the call. SetWindowPos returning
				// does not mean the owning app has finished processing WM_WINDOWPOSCHANGED,
				// and a window still reading as parked here has not moved yet.
				var rect = new Win32.Rect();
				Win32.GetWindowRect(hWnd, ref rect);

				Log.Frame("SHOWSTEP", $"0x{hWnd.ToInt64():X} lock={Ms(tEnter, tLock)} move={Ms(tLock, tMove)} style={Ms(tMove, tStyle)} alpha={Ms(tStyle, tAlpha)} raise={Ms(tAlpha, tRaise)} total={Ms(tEnter, tRaise)}ms at=({rect.Left},{rect.Top})");
			}
			finally
			{
				lockSem.Release();
			}
		}

		private static string Ms(long from, long to) =>
			((to - from) * 1000.0 / Stopwatch.Frequency).ToString("F2");

		public void Hide(IWindow window)
		{
			var hWnd = window.Handle;
            if(!Services.WindowIntegrity.CanControl(window.ProcessId))return;

            if(Services.NativeClientWindowPolicy.UsesNativeMinimize(window)) {
                if(!Services.WindowRestore.SameWindow(window) || !Win32.IsWindowVisible(hWnd) || window.IsMinimized)return;
                lock(_globalLock) { if(!_nativeStageMinimized.Add(hWnd))return; }
                if(!Win32.PostMessage(hWnd,Win32.WM_SYSCOMMAND,new IntPtr(0xF020),IntPtr.Zero)) {
                    lock(_globalLock) _nativeStageMinimized.Remove(hWnd);
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),"应用未接受收起请求。");
                }
                return;
            }

			if (ShouldSkipTransparencyForWindow(hWnd))
			{
				Log.Window("OPACITY", "Hide SKIPPED (invalid/minimized)", window);
				return;
			}

			var lockSem = _windowLocks.GetOrAdd(hWnd, _ => new System.Threading.SemaphoreSlim(1, 1));

			if(!lockSem.Wait(0)) return;
			try
			{
				if (ShouldSkipTransparencyForWindow(hWnd))
				{
					Log.Window("OPACITY", "Hide SKIPPED (double-check)", window);
					return;
				}

				var point = GetOffScreenPoint();
				Log.Window("OPACITY", $"Move off-screen to ({point.X},{point.Y})", window);
				lock (_globalLock)
				{
					if (!_originalPositions.ContainsKey(hWnd))
					{
						Win32.Rect rect = new Win32.Rect();
						Win32.GetWindowRect(hWnd, ref rect);
						_originalPositions[hWnd] = (rect.Left, rect.Top);
					}
				}

				// IgnoreZOrder matters: hWndInsertAfter of Zero is HWND_TOP, so without it every
				// park yanked the window to the FRONT of the z-chain on its way off-screen.
				// Parking is meant to be invisible, and the stacking a scene had when it was
				// last on screen is read back later to restore it (SceneManager.CaptureZOrder).
				Win32.SetWindowPos(hWnd, IntPtr.Zero,
					point.X, point.Y, 0, 0,
					Win32.SetWindowPosFlags.IgnoreResize |
					Win32.SetWindowPosFlags.IgnoreZOrder |
					Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.AsynchronousWindowPosition);


			}
			finally
			{
				lockSem.Release();
			}
		}
	}
}
