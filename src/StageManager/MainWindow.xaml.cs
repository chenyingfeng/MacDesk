using AsyncAwaitBestPractices;
using Microsoft.Xaml.Behaviors.Core;
using SharpHook;
using StageManager.Animations;
using StageManager.Controls;
using StageManager.Model;
using StageManager.Native;
using StageManager.Services;
using StageManager.Native.PInvoke;
using StageManager.Native.Interop;
using StageManager.Native.Window;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StageManager
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window, INotifyPropertyChanged
	{
		private const int TIMERINTERVAL_MILLISECONDS = 100;
		// Resting look of the tray, measured off macOS 26.5.2 to within
		// 0.2 pt RMS over 16 corners: every horizontal card edge tilts by
		// atan((yEdge - screenCenterY) / d), positive = right end rises, where
		// d = 1379 pt on a 1169 pt screen. Position drives the shape — angles do
		// NOT depend on row count or index. d scales with monitor height so the
		// look is resolution-independent; the pivot is the FULL monitor centre
		// (macOS pivots on screenHeight/2 including the menu bar), not the work
		// area centre. CompositionThumbnail turns each angle pair into the shear
		// + sprite rotation that reproduce exactly those two slopes.
		private const double EdgePerspectiveDistanceRatio = 1379.0 / 1169.0;
		private const string APP_NAME = "StageManager";
		// Fraction of sidebar width at which a normal window's left edge triggers auto-stow.
		// Lower = boundary sits further left, so wider windows keep the tray visible (more window estate).
		private const double STOW_OVERLAP_FRACTION = 0.25;
		// Mirrors CompositionThumbnail CornerRadius="8" in MainWindow.xaml so the
		// borrowed live drag ghost keeps the same rounded corners as the tray tile.
		private const double SidebarThumbCornerRadius = 8.0;
		private IntPtr _thisHandle;
        private PortableControl? _portableControl;
        private NonActivatingWindow? _nonActivatingWindow;
		private TaskPoolGlobalHook? _hook;
		private volatile bool _trayMenuOpen;
		private WindowMode _mode;
		// One-shot: snap the next sidebar mode change instead of sliding. Set when a
		// window is dropped into the tray so the new tile appears in place, not via unstow.
		private double _lastWidth;
		private Timer? _overlapCheckTimer;
		private long _mouseX;
		private CancellationTokenSource? _cancellationTokenSource;
		private SceneModel? _removedCurrentScene;
		// Guards SceneManager_CurrentSceneSelectionChanged against nested entry via
		// the synchronous CollectionChanged handlers it triggers.
		private bool _inSelectionChange;
		private bool _hideDesktopIcons;

		// WPF-native drag state (all UI thread, no cross-thread issues)
		private enum SidebarDragPhase { None, InSidebar, InBuffer, PastBuffer }
		private SceneModel? _wpfDragScene;
		private Point _wpfDragStartPoint;
        private readonly SidebarPointerGesture _sidebarPointer = new();
        private SidebarInputRoutes? _sidebarInputRoutes;
        private int _sidebarPressCount, _sidebarReleaseCount, _sidebarClickCount;
        private bool _sidebarNativeGesture;
		private SidebarDragPhase _sidebarDragPhase;
		private bool IsSidebarDragging => _sidebarDragPhase != SidebarDragPhase.None;
		private IWindow? _sidebarDragWindow;
		private Rect _sidebarDragThumbRect;
		private Rect _sidebarDragWindowRect;
		private Point _sidebarDragDpi;
		private double _sidebarDragBufferLeft;
		private double _sidebarDragBufferRight;
		private readonly SceneTransitionAnimator _sceneTransitionAnimator = new SceneTransitionAnimator();
		private readonly SidebarDragGhost _sidebarDragGhost;

		private DragDropManager? _dragDropManager;
		private readonly IconOverlayManager _iconOverlay = new();
		private readonly UpdateService _updateService = new();
		private string? _filterProcessKey;

		// Filter morph: enter decelerates (EaseOut), exit accelerates (EaseIn).
		private static readonly Duration _filterMorphDuration = new Duration(TimeSpan.FromMilliseconds(250));
		private static readonly IEasingFunction _filterEaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
		private static readonly IEasingFunction _filterEaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
		private readonly Dictionary<Guid, int> _filterAnimGen = new();

		// Grace window covering WindowsManager.MouseHook → DesktopShortClick latency (gated by
		// the OS double-click time, typically ~500ms). SharpHook clears the filter at T=0 but
		// the desktop toggle event fires later — predicate must report "filter was just cleared"
		// during the gap to suppress the toggle.
		private DateTime _filterClearedAt = DateTime.MinValue;
		private static readonly TimeSpan _filterClearGrace = TimeSpan.FromMilliseconds(750);

		// Flips true when the startup slide-in animation completes. Until then, OnRenderSizeChanged
		// must NOT yank Left back to 0 — the window is intentionally parked at -Width so DWM thumbnails
		// and scene previews stay culled while setup runs (scenes added, foreground scene switched).
		private bool _startupSlideComplete = false;
        private readonly LatestRequestQueue<Scene> _sidebarSwitches;
        private string _sidebarMessage="新打开的应用会自动登记";
        public string SidebarMessage {get=>_sidebarMessage;private set {_sidebarMessage=value;RaisePropertyChanged();}}
        public string ScenePageText {get;private set;}="";
        private bool _edgeArmed=true;
        private DateTime _sidebarSuppressedUntil=DateTime.MinValue;
        private bool _runtimeClosed;
        private int _sidebarRecoveryCount;

		public event PropertyChangedEventHandler? PropertyChanged;

		public bool EnableWindowPullToScene = false; // WPF owns sidebar drags; avoid a second hook-driven pull.

		public bool HideDesktopIcons
		{
			get => _hideDesktopIcons;
			set
			{
				if (_hideDesktopIcons != value)
				{
					_hideDesktopIcons = value;
					Settings.SetHideDesktopIcons(value);
					RaisePropertyChanged(nameof(HideDesktopIcons));

					// Apply setting change immediately
					ApplyDesktopIconsSetting();
				}
			}
		}

		public MainWindow()
		{
			_sidebarDragGhost = new SidebarDragGhost(_sceneTransitionAnimator);

			// Load initial setting BEFORE UI initialization
			_hideDesktopIcons = Settings.GetHideDesktopIcons();

			_sidebarSwitches = new LatestRequestQueue<Scene>(async scene => {
                if(SceneManager==null) return;
                await SceneManager.ActivateFromSidebar(scene);
                SidebarMessage=KeepWindowsVisible ? "已打开 · 其他窗口保留" : "已切换窗口组";
                // The second mouse-up of a double click can land on the desktop after
                // the sidebar hides. It must not immediately stow the scene again.
                SceneManager.WindowsManager.SuppressNextDesktopClick();
                HideSidebar();
                WriteRuntimeStatus("running");
            }, ex => {
                Log.Fatal("SIDEBAR","Switch failed: "+ex.Message);
                SidebarMessage="恢复失败："+ex.Message;
                Mode=WindowMode.Flyover;
            });
            InitializeComponent();
            _sidebarInputRoutes = new SidebarInputRoutes(scenesScroll,
                ScenesControl_PreviewMouseLeftButtonDown, ScenesControl_MouseMove,
                ScenesControl_PreviewMouseLeftButtonUp);
            scenesScroll.LostMouseCapture += (_, _) => {
                if (_wpfDragScene != null && Mouse.Captured != scenesScroll) CancelWpfDrag();
            };
            SourceInitialized += (_, _) => { _thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                _nonActivatingWindow = new NonActivatingWindow(this); _portableControl = new PortableControl(this); };
            Closed += (_, _) => { _sidebarInputRoutes?.Dispose(); _nonActivatingWindow?.Dispose(); _portableControl?.Dispose(); };

			// Set DataContext AFTER setting is loaded
			DataContext = this;

			_overlapCheckTimer = new Timer(OverlapCheck, null, 2500, TIMERINTERVAL_MILLISECONDS);

            SwitchSceneCommand = new ActionCommand(async model =>
            {
                if(model is not SceneModel sm || SceneManager==null) return;
                if(_filterProcessKey!=null) ClearAppFilter();
                await _sidebarSwitches.RequestAsync(sm.Scene);
            });


			_iconOverlay.OnSceneIconClicked = sm=>SwitchSceneCommand.Execute(sm);
		}

		private void ToggleAppFilter(string processKey)
		{
			var prior = _filterProcessKey;
			_filterProcessKey = (prior == processKey) ? null : processKey;
			_iconOverlay.HighlightedProcessKey = _filterProcessKey;

			var action = prior == null ? "SET"
				: _filterProcessKey == null ? "CLEAR (toggle off)"
				: "SWAP";
			Log.Info("FILTER", $"ToggleAppFilter: action={action} prior='{prior ?? "<none>"}' new='{_filterProcessKey ?? "<none>"}'");

			AnimateSyncVisibility();
		}

		private void ClearAppFilter()
		{
			if (_filterProcessKey == null) return;
			Log.Info("FILTER", $"ClearAppFilter: cleared filter='{_filterProcessKey}'");
			_filterClearedAt = DateTime.Now;
			_filterProcessKey = null;
			_iconOverlay.HighlightedProcessKey = null;
			AnimateSyncVisibility();
		}

		protected override void OnInitialized(EventArgs e)
		{
			base.OnInitialized(e);

			_thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			_lastWidth = Width;

			// Start hidden AND parked off-screen. Opacity=0 hides WPF content but DWM live thumbnails
			// follow window position regardless of opacity — Left=-Width keeps them culled by DWM
			// during setup so the user only sees the final slide-in.
			Opacity = 0;
			Left = SidebarHiddenLeft;

			StartHook();
		}

		/// <summary>
		/// Everything that talks to WinRT must be torn down HERE, not in OnClosed.
		/// OnClosing runs before WM_CLOSE destroys the window; OnClosed and the
		/// IsVisible cascade it triggers run *inside* the input-synchronous WM_CLOSE
		/// dispatch, where COM refuses outgoing cross-apartment calls with
		/// RPC_E_CANTCALLOUT_ININPUTSYNCCALL (0x8001010D). That throw used to kill the
		/// process mid-teardown, stranding every still-running GraphicsCaptureSession
		/// so DWM kept capturing those windows with nothing consuming the frames.
		/// </summary>
		protected override void OnClosing(CancelEventArgs e)
		{
			base.OnClosing(e);
			if (e.Cancel) return;
            _runtimeClosed=true;
            _groupsWindow?.Close();

			// Order matters: overlay/ghost first (they may hold a tile's borrowed
			// visual), then the tiles' own sessions.
			try { _sceneTransitionAnimator?.Dispose(); }
			catch (Exception ex) { Log.Info("SHUTDOWN", $"Animator dispose threw: {ex.Message}"); }

			try { _sidebarDragGhost?.Hide(); }
			catch (Exception ex) { Log.Info("SHUTDOWN", $"Drag ghost hide threw: {ex.Message}"); }

			try { CompositionThumbnail.ShutdownAll(); }
			catch (Exception ex) { Log.Info("SHUTDOWN", $"Capture shutdown threw: {ex.Message}"); }
		}

		protected override void OnClosed(EventArgs e)
		{
			// Cancel all background operations
			_cancellationTokenSource?.Cancel();
			_cancellationTokenSource?.Dispose();

			// Unsubscribe from SceneManager events before stopping to prevent memory leaks
			if (SceneManager != null) {
                SceneManager.SceneChanged -= SceneManager_SceneChanged;
                SceneManager.CurrentSceneSelectionChanged -= SceneManager_CurrentSceneSelectionChanged;
                SceneManager.WindowsManager.WindowUpdated -= OnWindowUpdatedForDrag;
            }

			StopHook();

			// Dispose the overlap check timer to stop background operations
			_overlapCheckTimer?.Dispose();

			trayIcon.Dispose();

			// Dispose SceneManager properly
			SceneManager?.Dispose();
            WriteRuntimeStatus("stopped");

			// Clean up animation overlay, drag ghost, and icon overlay
			_sceneTransitionAnimator?.Dispose();
			_iconOverlay?.Dispose();
			_updateService?.Dispose();

			base.OnClosed(e);
		}

		protected override async void OnContentRendered(EventArgs e)
		{
			base.OnContentRendered(e);

			Log.Info("STARTUP", "MainWindow content rendered, initializing...");

            WorkspaceEnvironment.Refresh(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            var windowsManager = new WindowsManager();
			SceneManager = new SceneManager(windowsManager, HideDesktopIcons);
            windowsManager.SystemNavigationChanged += (_, active) => {
                if (active) HideSidebar();
            };
			SceneManager.IsAppFilterActive = () =>
				_filterProcessKey != null
				|| (DateTime.Now - _filterClearedAt) < _filterClearGrace;

			// Ensure SceneManager.Start() is called on the main thread
			if (Dispatcher.CheckAccess())
			{
				await SceneManager.Start();
			}
			else
			{
				await Dispatcher.InvokeAsync(async () => await SceneManager.Start());
			}

            SceneManager.SelectWorkspace(_sidebarMonitor);
			SceneManager.SceneChanged += SceneManager_SceneChanged;
			SceneManager.CurrentSceneSelectionChanged += SceneManager_CurrentSceneSelectionChanged;
			SceneManager.AnimatedSwitch = scene => Dispatcher.InvokeAsync(() => SceneManager.SwitchTo(scene)).Task.Unwrap();

			// Wire up drag-and-drop manager
			_dragDropManager = new DragDropManager(
				SceneManager,
				_sidebarDragGhost,
				() => Dpi,
				() => _lastWidth,
				() => GetWorkAreaBounds(),
				w => WindowToLogicalRect(w),
				w => AllScenes.OfType<SceneModel>().SelectMany(s => s.Windows).FirstOrDefault(wm => wm.Handle == w.Handle)?.Icon,
				() => SyncVisibilityByUpdatedTimeStamp(),
				SidebarThumbCornerRadius);
			SceneManager.WindowsManager.WindowUpdated += OnWindowUpdatedForDrag;

			AddInitialScenes();

			// Pre-create the overlay window so the first animation has no HWND-creation lag
			// No resident fullscreen animation window. Drag proxy is created at card bounds only.
			

			// Initialize cancellation token source for background operations
			_cancellationTokenSource = new CancellationTokenSource();

			// Schedule a late initialization pass to recalculate thumbnail sizes after all window information is available.
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(2000, _cancellationTokenSource.Token).ConfigureAwait(false);
					if (!_cancellationTokenSource.Token.IsCancellationRequested)
					{
						Dispatcher.Invoke(() =>
						{
							foreach (var scene in Scenes)
								scene.UpdatePreviewSizes();
						});
					}
				}
				catch (OperationCanceledException)
				{
					// Expected during shutdown, ignore
				}
			});

			var foreground = Win32.GetForegroundWindow();
			var foregroundScene = SceneManager.FindSceneForWindow(foreground);
			if (foregroundScene is object)
				await SceneManager.SwitchTo(foregroundScene).ConfigureAwait(true);
            WriteRuntimeStatus("running");

            _startupSlideComplete=true;
            Opacity=1;
            _mode=WindowMode.OffScreen;
            Left=SidebarHiddenLeft;
            _iconOverlay.Enabled=false;
        }

		private void AddInitialScenes()
		{
			var initialScenes = SceneManager.GetScenes().ToArray();
			Log.Info("STARTUP", $"Adding {initialScenes.Length} initial scenes to sidebar");
			for (int i = 0; i < initialScenes.Length; i++)
			{
				var model = SceneModel.FromScene(initialScenes[i]);
                model.IsVisible=true;
				Scenes.Add(model);
				Log.Info("STARTUP", $"  Scene[{i}]: '{model.Title}' visible={model.IsVisible} windows={model.Windows.Count}");
			}

			RefreshIconOverlay();
		}

		private void SceneManager_CurrentSceneSelectionChanged(object? sender, CurrentSceneSelectionChangedEventArgs args)
		{
			// Ensure we are on the UI/Dispatcher thread before mutating observable collections bound to UI
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.Invoke(() => SceneManager_CurrentSceneSelectionChanged(sender, args));
				return;
			}

			// Re-entrancy: every mutation below raises CollectionChanged synchronously,
			// and its handlers can drive SceneManager back into another selection
			// change. A nested pass would edit Scenes / _removedCurrentScene on a
			// half-applied state — post it to run after this one instead.
			if (_inSelectionChange)
			{
				Log.Info("SIDEBAR", "SelectionChanged re-entered, deferring to next dispatcher slot");
				Dispatcher.BeginInvoke(new Action(() => SceneManager_CurrentSceneSelectionChanged(sender, args)));
				return;
			}

			_inSelectionChange = true;
			try { ApplyCurrentSceneSelection(args); }
			finally { _inSelectionChange = false; }
		}

        private void ApplyCurrentSceneSelection(CurrentSceneSelectionChangedEventArgs args)
        {
            // Keep stable item identities: removing/reinserting items disposed their capture
            // sessions in the mouse-up dispatch and caused repeated GPU/COM teardown.
            _removedCurrentScene = args.Current==null ? null : Scenes.FirstOrDefault(s=>s.Id==args.Current.Id);
            if(_removedCurrentScene!=null) _removedCurrentScene.Touch();
            SortRecentScenes();
            SyncVisibilityByUpdatedTimeStamp();
            RefreshIconOverlay();
        }



		protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
		{
			base.OnRenderSizeChanged(sizeInfo);
			
			// Stay parked off-screen until the startup slide-in animation finishes; otherwise this
			// firing during the initial layout pass yanks the window back to Left=0 and leaks the
			// pre-slide visual state (scenes appearing, active scene removal, icon flicker).
			BeginAnimation(LeftProperty,null);
            var work=GetWorkAreaBounds();
            this.Left = _startupSlideComplete && Mode!=WindowMode.OffScreen ? SidebarLeft : SidebarHiddenLeft;
            this.Top = work.Top;
            this.Height = work.Height;
			RefreshIconOverlay();
		}

		private void SceneManager_SceneChanged(object? sender, SceneChangedEventArgs e)
		{
			this.Dispatcher.BeginInvoke(new Action(() =>
			{
				Log.Info("UI", $"SceneChanged: {e.Change} scene='{e.Scene.Title}'");

				switch (e.Change)
				{
					case ChangeType.Created:
						if(!Scenes.Any(s=>s.Id==e.Scene.Id)) Scenes.Add(SceneModel.FromScene(e.Scene));
						SyncVisibilityByUpdatedTimeStamp();
						break;
					case ChangeType.Updated:
						if (AllScenes.FirstOrDefault(s => s?.Id == e.Scene.Id) is SceneModel toUpdate)
							toUpdate.UpdateFromScene(e.Scene);
						SyncVisibilityByUpdatedTimeStamp();
						break;
					case ChangeType.Removed:
                        if(IsSidebarDragging && _wpfDragScene?.Id==e.Scene.Id)CancelWpfDrag();
						if (AllScenes.FirstOrDefault(s => s?.Id == e.Scene.Id) is SceneModel toRemove)
						{
							if (toRemove.Equals(_removedCurrentScene)) _removedCurrentScene=null;
                            Scenes.Remove(toRemove);
						}
						SyncVisibilityByUpdatedTimeStamp();
						break;
				}

				RefreshIconOverlay();
			}));
		}

		private void OnWindowUpdatedForDrag(IWindow window, WindowUpdateType type)
		{
			switch (type)
			{
				case WindowUpdateType.MoveStart:
					Dispatcher.BeginInvoke(new Action(() => _dragDropManager?.OnWindowMoveStart(window)));
					break;
				case WindowUpdateType.MoveEnd:
					Dispatcher.BeginInvoke(new Action(() => _dragDropManager?.OnWindowMoveEnd(window)));
					break;
				case WindowUpdateType.Move:
					Dispatcher.BeginInvoke(new Action(() => _dragDropManager?.OnWindowMoved(window)));
					break;
			}
		}

        private void OnMousePressed(object? sender,MouseHookEventArgs e)
        {
            var p=new Point(e.Data.X,e.Data.Y);
            Dispatcher.BeginInvoke(new Action(()=> {
                if(!_trayMenuOpen && _filterProcessKey!=null && !IsPointInsideAnyVisibleScene(p)) ClearAppFilter();
            }));
        }
        private void OnMouseReleased(object? sender,MouseHookEventArgs e)
        {
            var x=e.Data.X;var y=e.Data.Y;
            var left=e.Data.Button==SharpHook.Data.MouseButton.Button1;
            Dispatcher.BeginInvoke(new Action(()=> {
                if(left && _sidebarNativeGesture) { NativeSidebarPointerUp(new Point(x,y));return; }
                if(!_trayMenuOpen) _dragDropManager?.OnGlobalMouseUp(x,y);
            }));
        }



		private SceneModel? FindSceneByPoint(Point p)
		{
			var thisWindow = new WindowsWindow(_thisHandle);
			var pointOnWindow = new Point(p.X - thisWindow.Location.X, p.Y - thisWindow.Location.Y);

			var dpi = Dpi;

			pointOnWindow.X /= dpi.X;
			pointOnWindow.Y /= dpi.Y;

			SceneModel? model = null;

			var element = VisualTreeHelper.HitTest(this, pointOnWindow)?.VisualHit;

			while (element is not null)
			{
				if (element is FrameworkElement { DataContext: SceneModel m })
				{
					model = m;
					break;
				}

				element = element.GetParentObject();
			}

			return model;
		}

		private bool IsPointInsideAnyVisibleScene(Point screenPoint)
		{
			foreach (var scene in Scenes)
			{
				if (!scene.IsVisible) continue;
				var rect = GetSceneThumbnailScreenBounds(scene);
				if (rect != Rect.Empty && rect.Contains(screenPoint))
					return true;
			}
			return false;
		}

		#region WPF Sidebar Drag (Flow 2: sidebar → active)

        private void ScenesControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_sidebarNativeGesture) return;
            if (BeginSidebarGesture(e.GetPosition(this), false, null)) e.Handled=true;
        }
        internal bool NativeSidebarPointerDown(Point screen, SceneModel? scene,IntPtr preferredHandle=default) =>
            BeginSidebarGesture(PointFromScreen(screen), true, scene,preferredHandle);
        private Native.Window.IWindow? _sidebarPressedWindow;
        private bool BeginSidebarGesture(Point local, bool native, SceneModel? knownScene,IntPtr preferredHandle=default)
        {
            _sidebarPressCount++;
            WriteSidebarInputStatus(native ? "native-press" : "wpf-press");
            if (_sidebarSwitches.IsBusy || _sceneTransitionAnimator.IsAnimating || _sidebarDragGhost.IsActive) return false;
            var scene = knownScene ?? FindSceneByPoint(PointToScreen(local));
            if (scene == null) {WriteSidebarInputStatus("press-no-card");return false;}
            _sidebarPointer.Begin(local, scenesScroll.VerticalOffset);
            _sidebarNativeGesture=native;
            _wpfDragScene=scene;
            _sidebarPressedWindow=scene.Scene.Windows.FirstOrDefault(w=>w.Handle==preferredHandle)
                ?? scene.Scene.Windows.LastOrDefault(w=>QqWindowPolicy.Retain(w));
            _wpfDragStartPoint=local;
            _sidebarDragPhase=SidebarDragPhase.None;
            _sidebarDragWindow=null;
            if (!native) Mouse.Capture(scenesScroll,CaptureMode.SubTree);
            return true;
        }
        internal void NativeSidebarPointerMove(Point screen,bool leftPressed) {
            if(_sidebarNativeGesture)MoveSidebarGesture(PointFromScreen(screen),leftPressed);
        }
        internal async void NativeSidebarPointerUp(Point screen) {
            if(!_sidebarNativeGesture)return;
            CompositionThumbnail.ReleaseNativeCapture();
            await EndSidebarGesture(PointFromScreen(screen));
        }
        internal void NativeSidebarPointerCancel() {if(_sidebarNativeGesture)CancelWpfDrag();}
        internal void NativeSidebarWheel(int delta) {
            if(IsSidebarDragging)return;
            if(_wpfDragScene!=null)CancelWpfDrag();
            ScrollSidebar(delta);
        }

        private void ScenesControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_sidebarNativeGesture) MoveSidebarGesture(e.GetPosition(this),e.LeftButton==MouseButtonState.Pressed);
        }
        private void MoveSidebarGesture(Point pos,bool leftPressed)
        {
			if (_wpfDragScene == null) return;
			if (!leftPressed)
			{
				CancelWpfDrag();
				return;
			}

            double outwardX=RightSidebarGeometry.OutwardDistance(Width,pos.X);

			if (!IsSidebarDragging)
			{
                _sidebarPointer.Move(pos);
                // Vertical movement belongs to scrolling, only outward horizontal movement starts a drag.
                if (!_sidebarPointer.IsOutwardDrag(pos)) return;

				_sidebarDragPhase = SidebarDragPhase.InSidebar;
				if (!_sidebarNativeGesture) Mouse.Capture(scenesScroll, CaptureMode.SubTree);
				Log.Info("DRAG", $"WPF drag started from '{_wpfDragScene.Title}'");

				// Resolve the window that will be popped (same as PopWindowFrom picks)
				_sidebarDragWindow = _sidebarPressedWindow;

				// Compute rects for interpolation
				_sidebarDragThumbRect = GetSceneThumbnailScreenBounds(_wpfDragScene);
				_sidebarDragWindowRect = _sidebarDragWindow != null
					? WindowToLogicalRect(_sidebarDragWindow)
					: Rect.Empty;
				if (_sidebarDragWindowRect == Rect.Empty)
					_sidebarDragWindowRect = new Rect(0, 0, 800, 600);

				_sidebarDragDpi = Dpi;
				_sidebarDragBufferLeft = _lastWidth;
				_sidebarDragBufferRight = _lastWidth + DragDropManager.BufferWidthLogical;

				var overlayBounds = GetWorkAreaBounds();
				if (_sidebarDragThumbRect != Rect.Empty && overlayBounds != Rect.Empty)
					_sidebarDragGhost.Show(overlayBounds, _sidebarDragThumbRect, _wpfDragScene,
						FindSceneThumbnail(_wpfDragScene, _sidebarDragWindow?.Handle ?? IntPtr.Zero),
						_sidebarDragDpi, SidebarThumbCornerRadius);
				else
					Log.Info("DRAG", $"Ghost skipped: overlay={overlayBounds == Rect.Empty} thumb={_sidebarDragThumbRect == Rect.Empty}");
			}

			if (!IsSidebarDragging) return;

			var screenPos = PointToScreen(pos);
			var dpi = _sidebarDragDpi;
			double cursorLogicalX = screenPos.X / dpi.X;
			double cursorLogicalY = screenPos.Y / dpi.Y;

			var prevPhase = _sidebarDragPhase;

			// Transition back from PastBuffer: hide real window, restore ghost
			if (_sidebarDragPhase == SidebarDragPhase.PastBuffer && outwardX <= _sidebarDragBufferRight)
			{
				_sidebarDragGhost.SetVisible(true);
				Log.Info("DRAG", $"Phase: PastBuffer → {(outwardX <= _sidebarDragBufferLeft ? "InSidebar" : "InBuffer")}");
			}

			if (outwardX <= _sidebarDragBufferLeft)
			{
				_sidebarDragPhase = SidebarDragPhase.InSidebar;

				_sidebarDragGhost.UpdatePositionAndSize(
					cursorLogicalX - _sidebarDragThumbRect.Width / 2,
					DragDropManager.CardTopFor(cursorLogicalY, _sidebarDragThumbRect.Height),
					_sidebarDragThumbRect.Width,
					_sidebarDragThumbRect.Height,
						CompositionThumbnail.TrayTiltDegrees);
			}
			else if (outwardX <= _sidebarDragBufferRight)
			{
				_sidebarDragPhase = SidebarDragPhase.InBuffer;

				double t = Math.Clamp((outwardX - _sidebarDragBufferLeft) / DragDropManager.BufferWidthLogical, 0.0, 1.0);
				double ghostW = DragDropManager.Lerp(_sidebarDragThumbRect.Width, _sidebarDragWindowRect.Width, t);
				double ghostH = DragDropManager.Lerp(_sidebarDragThumbRect.Height, _sidebarDragWindowRect.Height, t);

				_sidebarDragGhost.UpdatePositionAndSize(
					cursorLogicalX - ghostW / 2,
					DragDropManager.CardTopFor(cursorLogicalY, ghostH),
					ghostW, ghostH,
						DragDropManager.Lerp(CompositionThumbnail.TrayTiltDegrees, 0.0, t));
			}
			else
			{
                _sidebarDragPhase=SidebarDragPhase.PastBuffer;
                _sidebarDragGhost.SetVisible(true);
                _sidebarDragGhost.UpdatePositionAndSize(
                    cursorLogicalX-_sidebarDragWindowRect.Width/2,
                    DragDropManager.CardTopFor(cursorLogicalY,_sidebarDragWindowRect.Height),
                    _sidebarDragWindowRect.Width,_sidebarDragWindowRect.Height,0);
            }
        }

        private async void ScenesControl_PreviewMouseLeftButtonUp(object sender,MouseButtonEventArgs e)
        {
            if (_sidebarNativeGesture || _wpfDragScene==null) return;
            e.Handled=true;
            await EndSidebarGesture(e.GetPosition(this));
        }
        private async Task EndSidebarGesture(Point local)
        {
            _sidebarReleaseCount++;
            WriteSidebarInputStatus("release");
            if (!IsSidebarDragging) {
                var clicked = _wpfDragScene;
                bool activate = clicked != null && _sidebarPointer.End(local, scenesScroll.VerticalOffset)
                    && PointerInsideScene(clicked,PointToScreen(local));
                if (clicked == null) return;
                CancelWpfDrag();
                if (activate) {
                    _sidebarClickCount++;
                    WriteSidebarInputStatus("activate-request");
                    SwitchSceneCommand.Execute(clicked);
                }
                return;
            }
            var scene=_wpfDragScene?.Scene;
            var window=_sidebarDragWindow;
            var cursor=PointToScreen(local);
            bool commit=scene!=null && window!=null
                && RightSidebarGeometry.OutwardDistance(Width,local.X)>_sidebarDragBufferRight;
            var size=new Size(_sidebarDragWindowRect.Width*_sidebarDragDpi.X,_sidebarDragWindowRect.Height*_sidebarDragDpi.Y);
            var work=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)cursor.X,(int)cursor.Y)).WorkingArea;
            var target=RightSidebarGeometry.DropBounds(new Rect(work.X,work.Y,work.Width,work.Height),cursor,size);
            // Release the borrowed capture/visual only; do not cancel and repark a commit.
            CancelWpfDrag();
            if(!commit)return;
            try {
                await SceneManager.DropSceneFromSidebar(scene!,window!,target);
                SidebarMessage=MacMode?"窗口已加入当前任务或放到拖动位置":"窗口组已放到拖动位置";
                HideSidebar();WriteRuntimeStatus("running");
            } catch(Exception ex) {
                Log.Fatal("DROP","Drop failed: "+ex.Message);
                SidebarMessage="拖出失败："+ex.Message;Mode=WindowMode.Flyover;
            }
        }
        private void CancelWpfDrag()
        {
            _sidebarDragGhost.Hide();
            _sidebarDragPhase=SidebarDragPhase.None;
            _wpfDragScene=null;_sidebarDragWindow=null;_sidebarPressedWindow=null;
            _sidebarNativeGesture=false;
            _sidebarPointer.Cancel();
            CompositionThumbnail.ReleaseNativeCapture();
            if (Mouse.Captured == scenesScroll) Mouse.Capture(null);
            SceneManager.WindowsManager.SuppressNextDesktopClick();
        }
        private bool PointerInsideScene(SceneModel scene,Point screen) {
            if(ReferenceEquals(scene,FindSceneByPoint(screen)))return true;
            var dpi=Dpi;
            return GetSceneThumbnailScreenBounds(scene).Contains(new Point(screen.X/dpi.X,screen.Y/dpi.Y));
        }
        private void WriteSidebarInputStatus(string state)
        {
            try { File.WriteAllText(Path.Combine(PortablePreferences.Root,"sidebar-input-status.ini"),
                "Version="+System.Reflection.Assembly.GetExecutingAssembly().GetName().Version+"\nState="+state+"\nPresses="+_sidebarPressCount+"\nReleases="+_sidebarReleaseCount
                +"\nClickRequests="+_sidebarClickCount+"\nUtc="+DateTime.UtcNow.ToString("O")); } catch {}
        }



		#endregion

        private void SyncVisibilityByUpdatedTimeStamp()
        {
            foreach(var scene in Scenes) scene.IsVisible=(SceneManager==null || SceneManager.InSidebarWorkspace(scene.Scene,_sidebarMonitor)) && (!MacMode || SceneManager==null || !SceneManager.IsCurrentScene(scene.Scene))
                && (_filterProcessKey==null || scene.Windows.Any(w=>w.Window?.ProcessFileName==_filterProcessKey));
            AssignRowTilts();
        }
        internal Rect SidebarViewportPixels {
            get {
                if(scenesScroll==null || !scenesScroll.IsLoaded)return Rect.Empty;
                var a=scenesScroll.PointToScreen(new Point(0,0));
                var b=scenesScroll.PointToScreen(new Point(scenesScroll.ActualWidth,scenesScroll.ActualHeight));
                return new Rect(a,b);
            }
        }
        private void Sidebar_ScrollWheel(object sender,MouseWheelEventArgs e)
        {
            if(IsSidebarDragging)return;
            if (_wpfDragScene != null) CancelWpfDrag();
            ScrollSidebar(e.Delta);e.Handled=true;
        }
        internal void ScrollSidebar(double delta) {
            scenesScroll.ScrollToVerticalOffset(ThumbnailViewport.ScrollOffset(scenesScroll.VerticalOffset,delta,
                scenesScroll.ExtentHeight,scenesScroll.ViewportHeight));
        }
        private void Sidebar_ScrollChanged(object sender,ScrollChangedEventArgs e) {
            if (e.VerticalChange != 0) _sidebarPointer.Cancel();
            if(e.VerticalChange!=0 || e.ExtentHeightChange!=0)RefreshIconOverlay();
        }
        private void Sidebar_Previous_Click(object sender,RoutedEventArgs e)=>ScrollSidebar(240);
        private void Sidebar_Next_Click(object sender,RoutedEventArgs e)=>ScrollSidebar(-240);



		// Assigns each visible scene the top/bottom edge angles the macOS position
		// law dictates for its on-screen location (see EdgePerspectiveDistanceRatio).
		// Angles need final layout positions, so the real work runs at Loaded
		// priority — after the layout pass that the triggering data change caused.
		// Tilts are a composition-only transform (no layout input), so measuring
		// post-layout cannot feed back into another pass.
		private bool _tiltAssignQueued;
		private void AssignRowTilts()
		{
			if (_tiltAssignQueued) return;
			_tiltAssignQueued = true;
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
			{
				_tiltAssignQueued = false;
				AssignRowTiltsCore();
			}));
		}

		private void AssignRowTiltsCore()
		{
			double screenH = SidebarScreen.Bounds.Height / Dpi.Y;
			double centerY = screenH / 2.0;
			double d = screenH * EdgePerspectiveDistanceRatio;

			foreach (var scene in Scenes.Where(s => s.IsVisible))
			{
				var container = TryGetSceneItemContainer(scene);
				if (container is null || TryGetSceneInnerGrid(scene) is not FrameworkElement inner || inner.ActualHeight <= 0)
					continue;
				if (System.Windows.Media.VisualTreeHelper.GetParent(container) is not UIElement panel)
					continue;

				// Layout slot, not TransformToVisual: the FLIP slide animates the
				// container's RenderTransform from the OLD position to 0, so a visual
				// measurement mid-flight would bake in a stale offset that nothing
				// recomputes once the animation lands. The slot is already final here.
				var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(container);
				Point panelOrigin;
				try { panelOrigin = panel.TransformToVisual(this).Transform(new Point(0, 0)); }
				catch (InvalidOperationException) { continue; }

				double windowTop = (SidebarScreen.WorkingArea.Top-SidebarScreen.Bounds.Top) / Dpi.Y;
				double yTop = windowTop + panelOrigin.Y + slot.Y;
				double yBottom = yTop + inner.ActualHeight;

				// centerY / d above are kept for the log line only — the geometry
				// measurement pass parses them. The angles themselves come from the
				// shared law so the flying card can solve its own from the same source.
				scene.TiltTopDegrees = SceneModel.EdgeTiltDegreesAt(yTop);
				scene.TiltBottomDegrees = SceneModel.EdgeTiltDegreesAt(yBottom);
				// Invariant format - parsed by the geometry measurement pass.
				Log.Info("TILT", FormattableString.Invariant($"scene='{scene.Title}' yTop={yTop:F1} yBottom={yBottom:F1} centerY={centerY:F1} d={d:F1} top={scene.TiltTopDegrees:F3} bottom={scene.TiltBottomDegrees:F3}"));
			}
		}

		// Animated counterpart to SyncVisibilityByUpdatedTimeStamp for filter-toggle paths.
        private void AnimateSyncVisibility()
        {
            SyncVisibilityByUpdatedTimeStamp();RefreshIconOverlay();
        }

		private void AnimateSceneEnter(SceneModel s)
		{
			var gen = NextFilterGen(s.Id);
			s.IsVisible = true;
			if (TryGetSceneInnerGrid(s) is FrameworkElement inner) { StartEnterAnimation(inner); return; }
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
			{
				if (CurrentFilterGen(s.Id) != gen) return;
				if (TryGetSceneInnerGrid(s) is FrameworkElement inner2)
					StartEnterAnimation(inner2);
			});
		}

		// From=0 explicit; FillBehavior.Stop reverts to style-base Opacity=0.8.
		private static void StartEnterAnimation(FrameworkElement inner) =>
			inner.BeginAnimation(UIElement.OpacityProperty,
				Anim.From(0, 0.8, _filterMorphDuration, _filterEaseOut, FillBehavior.Stop));

		private void AnimateSceneExit(SceneModel s, TaskCompletionSource? tcs = null)
		{
			var gen = NextFilterGen(s.Id);
			if (TryGetSceneInnerGrid(s) is not FrameworkElement inner)
			{
				s.IsVisible = false;
				tcs?.TrySetResult();
				return;
			}
			// HoldEnd keeps Opacity=0 painted until IsVisible flips — no flash before collapse.
			var opacityAnim = Anim.To(0, _filterMorphDuration, _filterEaseIn);
			opacityAnim.Completed += (_, _) =>
			{
				if (CurrentFilterGen(s.Id) == gen)
					s.IsVisible = false;
				tcs?.TrySetResult();
			};
			inner.BeginAnimation(UIElement.OpacityProperty, opacityAnim, HandoffBehavior.SnapshotAndReplace);
		}

		// FLIP target = ContentPresenter, not inner Grid. The Grid carries the SceneOpacity
		// style's ScaleTransform consumed by the hover storyboard — replacing its
		// RenderTransform would break `(RenderTransform).(ScaleTransform.ScaleX)` resolution.
		private void RestoreSceneVisible(SceneModel s, double oldYAbs = double.NaN)
		{
			NextFilterGen(s.Id);
			if (TryGetSceneInnerGrid(s) is not FrameworkElement inner) return;
			inner.BeginAnimation(UIElement.OpacityProperty, Anim.To(0.8, _filterMorphDuration, _filterEaseOut, FillBehavior.Stop), HandoffBehavior.SnapshotAndReplace);

			if (double.IsNaN(oldYAbs)) return;

			var container = TryGetSceneItemContainer(s);
			if (container is null) return;

			double currentLayoutY = GetSceneInnerY(s);
			if (double.IsNaN(currentLayoutY)) return;
			double from = oldYAbs - currentLayoutY;

			if (Math.Abs(from) > 0.5)
			{
				// Pre-seed local Y = from BEFORE BeginAnimation. Without this, render thread
				// can paint one frame at the layout-only position (local Y=0 → visual at newY)
				// before the animation clock ticks, causing a forward-back-forward visual dance.
				// HoldEnd holds animated value at To=0 indefinitely so the seeded local doesn't
				// resurface post-animation; SnapshotAndReplace keeps subsequent cycles clean.
				if (container.RenderTransform is not TranslateTransform tt)
				{
					tt = new TranslateTransform { Y = from };
					container.RenderTransform = tt;
				}
				else
				{
					tt.BeginAnimation(TranslateTransform.YProperty, null);
					tt.Y = from;
				}
				var slide = Anim.From(from, 0, _filterMorphDuration, _filterEaseOut);
				tt.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
			}
		}

		private double GetSceneInnerY(SceneModel s)
		{
			var container = TryGetSceneItemContainer(s);
			if (container is null) return double.NaN;
			try { return container.TransformToVisual(this).Transform(new Point(0, 0)).Y; }
			catch { return double.NaN; }
		}

		private FrameworkElement? TryGetSceneItemContainer(SceneModel s)
			=> scenesControl.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement;

		private int NextFilterGen(Guid id)
		{
			var v = (_filterAnimGen.TryGetValue(id, out var c) ? c : 0) + 1;
			_filterAnimGen[id] = v;
			return v;
		}

		private int CurrentFilterGen(Guid id) => _filterAnimGen.TryGetValue(id, out var c) ? c : 0;

		/// <summary>
		/// Locate the live tile (CompositionThumbnail) for a specific window inside
		/// a scene's container, matched by capture handle. Null if the container
		/// isn't realised or no tile owns that handle.
		/// </summary>
		private CompositionThumbnail? FindSceneThumbnail(SceneModel scene, IntPtr handle)
		{
			if (handle == IntPtr.Zero) return null;
			var container = scenesControl.ItemContainerGenerator.ContainerFromItem(scene) as DependencyObject;
			if (container is null) return null;
			foreach (var ct in EnumerateVisualDescendants<CompositionThumbnail>(container))
				if (ct.PreviewHandle == handle) return ct;
			return null;
		}

		private static System.Collections.Generic.IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject root)
			where T : DependencyObject
		{
			int count = VisualTreeHelper.GetChildrenCount(root);
			for (int i = 0; i < count; i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				if (child is T match) yield return match;
				foreach (var nested in EnumerateVisualDescendants<T>(child))
					yield return nested;
			}
		}

		private FrameworkElement? TryGetSceneInnerGrid(SceneModel s)
		{
			var container = scenesControl.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement;
			if (container == null) return null;
			if (VisualTreeHelper.GetChildrenCount(container) == 0) return null;
			return VisualTreeHelper.GetChild(container, 0) as FrameworkElement;
		}

		/// <summary>
		/// Return every scene tile to its resting scale. Matches the shape the SceneOpacity
		/// style installs: a ScaleTransform carrying the 1.08 hover pop.
		/// <para>
		/// It only ever unwinds when the cursor leaves the tile, via the trigger's ExitActions,
		/// and holds its last value indefinitely when that never happens. Two paths take the
		/// tile out from under a resting cursor without firing it: the sidebar stowing, and a
		/// scene switch hiding and rebuilding the tiles. The tile is then left popped out.
		/// </para>
		/// </summary>
		private void ResetAllSceneHoverTransforms()
		{
			foreach (var grid in EnumerateVisualDescendants<Grid>(scenesControl))
			{
				// The style's transform is a shared frozen instance until a storyboard clones it
				// per element, so a tile that was never hovered is read-only — and already resting.
				if (grid.RenderTransform is not ScaleTransform st || st.IsFrozen) continue;
				if (st.ScaleX == 1 && st.ScaleY == 1) continue;

				// The hover storyboard holds 1.08 with FillBehavior.HoldEnd. Detaching the
				// clock first is what lets the local write below take effect.
				st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				st.ScaleX = 1;
				st.ScaleY = 1;
			}
		}

		private Rect SidebarViewportLogical {
            get { var r = SidebarViewportPixels; var dpi = Dpi;
                return r.IsEmpty ? Rect.Empty : new Rect(r.X / dpi.X, r.Y / dpi.Y, r.Width / dpi.X, r.Height / dpi.Y); }
        }

		private void RefreshIconOverlay(double xOffset = 0)
		{
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
			{
				// Flush sidebar layout — Remove+Insert during selection invalidates async.
				UpdateLayout();
				var visible = Scenes.Where(s => s.IsVisible).ToList();
				_iconOverlay.UpdateIcons(visible, s => {
                    var r=GetSceneThumbnailScreenBounds(s);
                    if(r.IsEmpty)return Rect.Empty;
                    var viewport=SidebarViewportPixels;
                    if(viewport.IsEmpty)return Rect.Empty;
                    var dpi=Dpi;
                    var logical=new Rect(viewport.X/dpi.X,viewport.Y/dpi.Y,viewport.Width/dpi.X,viewport.Height/dpi.Y);
                    return logical.Contains(r) ? r : Rect.Empty;
                }, SidebarViewportLogical, xOffset);
			});
		}

		public ObservableCollection<SceneModel> Scenes { get; } = new ObservableCollection<SceneModel>();

		public IEnumerable<SceneModel?> AllScenes => Scenes.Union(new[] { _removedCurrentScene });

		public bool KeepWindowsVisible {
            get=>!MacMode && !PortablePreferences.Read("exclusive-stage");
            set {
                PortablePreferences.Write("exclusive-stage",!value);
                if(value && MacMode) {PortablePreferences.Write("mac-mode",false);RaisePropertyChanged(nameof(MacMode));}
                ApplyStageMode();
                PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(KeepWindowsVisible)));
            }
        }
        private async void Sidebar_Tile_Click(object sender,RoutedEventArgs e)
        {
            if(SceneManager==null)return;
            try {
                var work=SidebarScreen.WorkingArea;
                await SceneManager.ArrangeStageAsync(new Rect(work.X,work.Y,work.Width,work.Height));
                SidebarMessage="已并排显示当前窗口";
                HideSidebar();
            } catch(Exception ex) {SidebarMessage="并排显示失败："+ex.Message;Mode=WindowMode.Flyover;}
        }
        public ICommand SwitchSceneCommand { get; }

		public SceneManager SceneManager { get; private set; } = null!;

		public IntPtr Handle => _thisHandle;

		public WindowMode Mode
		{
			get => _mode;
			set
			{
				if (value == _mode)
					return;

				Log.Info("MODE", $"Sidebar mode: {_mode} → {value}");

				_mode = value;

				this.Topmost = value == WindowMode.Flyover;

				ApplyWindowMode();
			}
		}

        private double SidebarLeft => GetWorkAreaBounds().Right-Width;
        private double SidebarHiddenLeft => System.Windows.Forms.SystemInformation.VirtualScreen.Right / Dpi.X + 40;
        private double SidebarRightPhysical => SidebarScreen.WorkingArea.Right;

        private void ApplyWindowMode()
        {
            bool visible=Mode!=WindowMode.OffScreen;
            double target=visible ? SidebarLeft : SidebarHiddenLeft;
            if(!visible) ResetAllSceneHoverTransforms();
            BeginAnimation(LeftProperty,null);
            _iconOverlay.Enabled=visible;
            // Keep icons and capture HWNDs at the same final geometry during transitions.
            // A short WPF fade avoids independent slide clocks fighting on rapid hover.
            Left=target;PlaceSidebarNative();
            foreach(var tile in EnumerateVisualDescendants<CompositionThumbnail>(scenesControl))tile.RefreshDwm();
            if(visible) {
                UpdateLayout(); RefreshIconOverlay(); _iconOverlay.BringToFront();
                BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(140)));
            } else {
                BeginAnimation(OpacityProperty,null); Opacity=1;
                _iconOverlay.Hide();
            }
        }



		private void StartHook()
		{
			_hook = new TaskPoolGlobalHook();

			_hook.MousePressed += OnMousePressed;
			_hook.MouseReleased += OnMouseReleased;
			_hook.MouseMoved += _hook_MouseMoved;

			Task.Run(_hook.Run);
		}

		private void StopHook()
		{
			if (_hook is null) return;
			_hook.MousePressed -= OnMousePressed;
			_hook.MouseReleased -= OnMouseReleased;
			_hook.MouseMoved -= _hook_MouseMoved;

			try
			{
				_hook.Dispose();
			}
			catch (HookException)
			{
			}
		}

        private int _hoverUpdateQueued;
        private DateTime _edgeHoverSince=DateTime.MinValue;
        private void _hook_MouseMoved(object? sender,MouseHookEventArgs e)
        {
            Interlocked.Exchange(ref _mouseX,e.Data.X);
            QueueHoverUpdate();
        }
        private void OverlapCheck(object? _) => QueueHoverUpdate();
        private void QueueHoverUpdate()
        {
            if(_runtimeClosed||Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _hoverUpdateQueued,1)!=0) return;
            try {
                Dispatcher.BeginInvoke(new Action(()=> {
                    Interlocked.Exchange(ref _hoverUpdateQueued,0);
                    if(_runtimeClosed)return;
                    try {
                        if(_sidebarNativeGesture && Win32.GetCursorPos(out var nativePoint))
                            NativeSidebarPointerMove(new Point(nativePoint.X,nativePoint.Y),true);
                        UpdateModeByWindows(Array.Empty<IWindow>());
                    }catch(Exception error) when(error is System.Runtime.InteropServices.COMException
                        ||error is System.ComponentModel.Win32Exception||error is IOException||error is UnauthorizedAccessException) {
                        (Application.Current as App)?.UiHealth?.MaintenanceFault("SidebarHover",error);
                        _edgeArmed=true;_edgeHoverSince=DateTime.MinValue;
                    }
                }));
            }catch(InvalidOperationException) when(_runtimeClosed||Dispatcher.HasShutdownStarted) {
                Interlocked.Exchange(ref _hoverUpdateQueued,0);
            }
        }
        private static bool IsTaskbarPoint(Win32.POINT p) {
            var h=Win32.WindowFromPoint(new System.Drawing.Point(p.X,p.Y));
            var root=TaskbarRoot(h,2);
            var cls=StageManager.Helpers.DesktopShellClassifier.GetClassName(root);
            return cls=="Shell_TrayWnd" || cls=="Shell_SecondaryTrayWnd";
        }
        [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetAncestor")]
        private static extern IntPtr TaskbarRoot(IntPtr h,uint flags);
        private readonly StageManager.Services.SidebarResumePolicy _shellResume=new();
        private long _nextTaskbarStateRead;
        private bool _taskbarPeek;
        private void UpdateModeByWindows(IEnumerable<IWindow> windows)
        {
            if(SceneManager==null) return;
            if(Environment.TickCount64>=_nextTaskbarStateRead) {
                _nextTaskbarStateRead=Environment.TickCount64+150;
                try {_taskbarPeek=RuntimeSnapshotPolicy.TaskbarPeek(File.ReadAllText(Path.Combine(PortablePreferences.Root,"taskbar-status.ini")),DateTime.UtcNow);}
                catch(IOException){_taskbarPeek=false;}catch(UnauthorizedAccessException){_taskbarPeek=false;}
            }
            bool navigation=SceneManager.WindowsManager.SystemNavigationActive;
            if(_shellResume.Observe(navigation,_taskbarPeek)) {
                _edgeArmed=true;_edgeHoverSince=DateTime.MinValue;_sidebarSuppressedUntil=DateTime.MinValue;
            }
            if(navigation) { if(Mode!=WindowMode.OffScreen)HideSidebar(); return; }
            // Menu Closed can be missed during a shell/desktop change. Actual menu
            // lifetime is authoritative; a stale event flag cannot disable reveal forever.
            if(_trayMenuOpen && trayIcon.ContextMenu?.IsOpen!=true)_trayMenuOpen=false;
            if(_trayMenuOpen || _groupsWindow!=null || IsSidebarDragging || _dragDropManager?.IsDragging==true) return;
            if(!Win32.GetCursorPos(out var p)) return;
            RefreshWorkspaceForPointer(p);
            if(WorkspaceEnvironment.ForegroundFullscreen(_sidebarMonitor)){HideSidebar();return;}
            var bounds=SidebarScreen.Bounds;
            var work=SidebarScreen.WorkingArea;
            double right=SidebarRightPhysical;
            double left=RightSidebarGeometry.PhysicalLeft(right,Math.Max(48,_lastWidth),Dpi.X);
            bool inDisplay=p.Y>=work.Top && p.Y<work.Bottom && p.X>=work.Left && p.X<work.Right;
            // Entering the taskbar hides the topmost flyover immediately; it must not
            // count as hovering the right-edge reveal zone (also with auto-hide taskbars).
            if(IsTaskbarPoint(p) || !inDisplay || SceneManager.WindowsManager.IsCompanionWindow(
                Win32.WindowFromPoint(new System.Drawing.Point(p.X,p.Y)))) {
                _edgeHoverSince=DateTime.MinValue;_edgeArmed=true;
                if(Mode!=WindowMode.OffScreen)Mode=WindowMode.OffScreen;
                return;
            }
            if(SidebarAlwaysVisible && DateTime.UtcNow>=_sidebarSuppressedUntil) {if(Mode!=WindowMode.Flyover)Mode=WindowMode.Flyover;return;}
            bool nearEdge=inDisplay && p.X>=right-6*Dpi.X && p.X<bounds.Right;
            bool inside=inDisplay && p.X>=left-12*Dpi.X && p.X<bounds.Right;
            if(!nearEdge) { _edgeArmed=true; _edgeHoverSince=DateTime.MinValue; }
            if(DateTime.UtcNow<_sidebarSuppressedUntil) return;
            if(RuntimeSnapshotPolicy.ShouldRearm(Mode==WindowMode.OffScreen,_edgeArmed,nearEdge,_sidebarSuppressedUntil,DateTime.UtcNow))
                _edgeArmed=true;
            if(Mode!=WindowMode.OffScreen && !inside) { Mode=WindowMode.OffScreen; return; }
            if(Mode==WindowMode.OffScreen && nearEdge && _edgeArmed) {
                if(_edgeHoverSince==DateTime.MinValue) _edgeHoverSince=DateTime.UtcNow;
                if((DateTime.UtcNow-_edgeHoverSince).TotalMilliseconds>=180) Mode=WindowMode.Flyover;
            }
        }
        internal void HideSidebar()
        {
            _edgeArmed=false;_edgeHoverSince=DateTime.MinValue;
            _sidebarSuppressedUntil=DateTime.UtcNow.AddMilliseconds(350);
            Mode=WindowMode.OffScreen;
        }
        internal void RearmSidebar()
        {
            if(_runtimeClosed)return;
            _edgeArmed=true;_edgeHoverSince=DateTime.MinValue;_sidebarSuppressedUntil=DateTime.MinValue;
            _sidebarRecoveryCount++;
            QueueHoverUpdate();WriteRuntimeHeartbeat();
        }
        internal async void StowCurrentGroup()
        {
            try { if(SceneManager!=null) await SceneManager.StowCurrentScene(); }
            catch(Exception ex) {Log.Fatal("SIDEBAR","Stow failed: "+ex.Message);}
            HideSidebar();WriteRuntimeStatus("running");
        }
        private void Sidebar_Hide_Click(object sender,RoutedEventArgs e) => HideSidebar();
        private void Sidebar_Stow_Click(object sender,RoutedEventArgs e) => StowCurrentGroup();




		/// <summary>
		/// DPI scale factors for converting between physical and logical coordinates.
		/// Cached lazily; invalidated on <see cref="OnDpiChanged"/>.
		/// </summary>
		private Point _dpi = new(1.0, 1.0);
		private bool _dpiCached;

		// Snapshot of Dpi.X for the SharpHook callbacks. They run off the UI thread and
		// hook coordinates are physical, so anything they compare against a WPF DIP width
		// has to be scaled — see OnMouseReleased.
		private double _lastDpiX = 1.0;

		private Point Dpi
		{
			get
			{
				if (_dpiCached) return _dpi;
				var source = PresentationSource.FromVisual(this);
				if (source?.CompositionTarget == null)
					return _dpi; // pre-source-init: return fallback but don't cache it
				_dpi = new Point(source.CompositionTarget.TransformToDevice.M11, source.CompositionTarget.TransformToDevice.M22);
				_dpiCached = true;
				_lastDpiX = _dpi.X;
				return _dpi;
			}
		}

		protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
		{
			base.OnDpiChanged(oldDpi, newDpi);
			_dpiCached = false;
            Dispatcher.BeginInvoke(new Action(()=>{PlaceSidebarNative();foreach(var model in Scenes)model.UpdatePreviewSizes();}));
		}

		/// <summary>
		/// Returns the screen bounds of a scene's thumbnail in WPF logical (DPI-independent) units.
		/// </summary>
		private Rect GetSceneThumbnailScreenBounds(SceneModel sceneModel)
		{
			try
			{
				var container = scenesControl.ItemContainerGenerator.ContainerFromItem(sceneModel) as FrameworkElement;
				if (container == null)
					return Rect.Empty;

				var dpi = Dpi;

				// Get screen coordinates (physical pixels) then convert to logical units
				var topLeft = container.TranslatePoint(new Point(0, 0), this);
				var bottomRight = container.TranslatePoint(new Point(container.ActualWidth, container.ActualHeight), this);

				var screenTopLeft = PointToScreen(topLeft);
				var screenBottomRight = PointToScreen(bottomRight);

				return new Rect(
					screenTopLeft.X / dpi.X,
					screenTopLeft.Y / dpi.Y,
					(screenBottomRight.X - screenTopLeft.X) / dpi.X,
					(screenBottomRight.Y - screenTopLeft.Y) / dpi.Y);
			}
			catch
			{
				return Rect.Empty;
			}
		}

		/// <summary>
		/// Converts a window's Location (physical pixels) to WPF logical units.
		/// Returns Rect.Empty if the window is minimized, offscreen-parked, or invalid.
		/// </summary>
		private Rect WindowToLogicalRect(Native.Window.IWindow? window)
		{
			if (window == null || !WindowRestore.SameWindow(window)) return Rect.Empty;
            if(window.IsMinimized) {
                var normal=WindowRestoreGeometry.NormalSize(window.Handle,new Size(800*Dpi.X,600*Dpi.Y));
                return new Rect(0,0,normal.Width/Dpi.X,normal.Height/Dpi.Y);
            }

			var loc = window.Location;
			if (loc.Width <= 0 || loc.Height <= 0)
				return Rect.Empty;

			// If OpacityWindowStrategy has parked this window off-screen, use the saved
			// original position so the animator targets the on-screen rect the window will
			// occupy after Show — not its current parked location.
			int x = loc.X, y = loc.Y;
			if (Strategies.OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out var ox, out var oy))
			{
				x = ox; y = oy;
			}
			else if (loc.X < -10000)
			{
				return Rect.Empty;
			}

			var dpi = Dpi;
			return new Rect(x / dpi.X, y / dpi.Y, loc.Width / dpi.X, loc.Height / dpi.Y);
		}

		private Rect GetSceneWindowBounds(SceneModel sceneModel)
		{
			var window = sceneModel.Scene.Windows.FirstOrDefault(w => !w.IsMinimized);
			var rect = WindowToLogicalRect(window);
			return rect != Rect.Empty ? rect : GetWorkAreaBounds();
		}

		private Rect GetCurrentSceneWindowBounds()
		{
			var window = SceneManager.GetCurrentWindows().FirstOrDefault(w => !w.IsMinimized);
			return WindowToLogicalRect(window);
		}

		/// <summary>
		/// Returns the monitor work area in WPF logical (DPI-independent) units. Used as fallback.
		/// </summary>
        private Rect GetWorkAreaBounds()
        {
            // The monitor follows the pointer; parking the HWND never chooses its workspace.
            var r=SidebarScreen.WorkingArea;
            return new Rect(r.Left/Dpi.X,r.Top/Dpi.Y,r.Width/Dpi.X,r.Height/Dpi.Y);
        }


		private void NavigateToProjectPage()
		{
			Process.Start(new ProcessStartInfo("https://github.com/depoledna/StageManagerForWindows")
			{
				UseShellExecute = true
			});
		}

		public static bool StartsWithWindows
		{
			get => AutoStart.IsStartup(APP_NAME);
			set => AutoStart.SetStartup(APP_NAME, value);
		}

		private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(memberName));
		}

		private void ApplyDesktopIconsSetting()
		{
			if (SceneManager == null) return;

            SceneManager.SetDesktopIconsPreference(_hideDesktopIcons);
		}

		private void MenuItem_ProjectPage_Click(object sender, RoutedEventArgs e)
		{
			NavigateToProjectPage();
		}

		private enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }

		private UpdateState _updateState = UpdateState.Idle;
		private UpdateInfo? _availableUpdate;
		private double _downloadProgress;
		private string? _downloadedPath;
		private readonly string _currentVersionString = UpdateService.GetCurrentVersion().ToString();

		public string AppHeaderText => $"MacDesk · v{_currentVersionString}";

        private void WriteRuntimeStatus(string state)
        {
            try {
                var path=Path.Combine(AppContext.BaseDirectory,"runtime-status.ini");
                File.WriteAllText(path+".pending",
                    "Version="+_currentVersionString+"\nPid="+Environment.ProcessId+"\nCallerIntegrity="+WindowIntegrity.Read(Environment.ProcessId)+"\nState="+state+"\nScenes="+(SceneManager?.GetScenes().Count() ?? 0)
                    +"\nTrackedWindows="+(SceneManager?.WindowsManager.Windows.Count() ?? 0)
                    +"\nPreviewBackend=DWM\nWgcCodeIncluded=False\nFullscreenOverlay=False\nActiveThumbnails="+StageManager.Composition.DwmPreviewSurface.ActiveRelations
                    +"\nHotkeyReady="+(_portableControl?.HotkeyReady ?? false)
                    +"\nSidebarStows="+(SceneManager?.SidebarStows ?? 0)+"\nSidebarActivations="+(SceneManager?.SidebarActivations ?? 0)+"\nSystemSelections="+(SceneManager?.SystemSelections ?? 0)+"\nRestoreFailures="+(SceneManager?.RestoreFailures ?? 0)
                    +"\nSystemNavigationActive="+(SceneManager?.WindowsManager.SystemNavigationActive ?? false)+"\nSidebarMode="+Mode+"\nSidebarRevealArmed="+_edgeArmed+"\nTaskbarPeek="+_taskbarPeek
                    +"\nSidebarRecoveryRequests="+_sidebarRecoveryCount
                    +"\nDesktopQueryResponsive="+WorkspaceEnvironment.DesktopQueryResponsive+"\nDesktopQueriesPending="+WorkspaceEnvironment.DesktopQueriesPending
                    +"\nUtc="+DateTime.UtcNow.ToString("o")+"\n");
                File.Move(path+".pending",path,true);
            } catch { }
        }
        internal void WriteRuntimeHeartbeat(){if(!_runtimeClosed)WriteRuntimeStatus(SceneManager==null?"starting":"running");}
        private void MenuItem_Help_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("自动按应用分组，默认允许多个应用及同一应用的多个窗口同时显示。无需进入设置、命名或保存分组。\n\n右边缘：悬停展开侧栏，点击恢复并保留其他台前窗口；滚轮或双指上下滑动浏览。\n把缩略图拖入当前任务可同时显示多个应用；最小化成员可收回侧栏。\n底部程序坞常驻：点击图标恢复或打开应用；右键可选择应用的其他窗口。\n任务栏 / 托盘按钮：点一次显示，再点一次隐藏；由系统托盘找回已登录的 QQ / 微信。\n\n全屏时侧栏与 Dock 退让；退出全屏后恢复。\n各显示器和虚拟桌面分别保留任务，虚拟桌面仍用系统任务视图或 Ctrl+Win+方向键切换。\nCtrl+Alt+Shift+D 显示桌面 / 返回任务；G 打开本说明。\nCtrl+Alt+Shift+F11 紧急恢复任务栏；F12 退出并恢复桌面。", "台前调度使用说明");
        }

		public string UpdateMenuText => _updateState switch
		{
			UpdateState.Idle => "更新说明 / Manual updates",
			UpdateState.Checking => "Checking...",
			UpdateState.UpToDate => "Up to date",
			UpdateState.Available => $"Update to {_availableUpdate!.TagName}",
			UpdateState.Downloading => $"Downloading...  {_downloadProgress:P0}",
			UpdateState.Ready => "Restart to update",
			UpdateState.Error => "Update failed \u00b7 Retry",
			_ => "Check for updates"
		};

		private void SetUpdateState(UpdateState state)
		{
			_updateState = state;
			RaisePropertyChanged(nameof(UpdateMenuText));
		}

		private void MenuItem_CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("MacDesk 使用完整发布包手动更新，不会自动下载或替换程序。请退出后再解压新版本，并保留自己的配置。", "MacDesk 更新");
        }

        private async Task PerformUpdateCheckAsync()
		{
			SetUpdateState(UpdateState.Checking);
			try
			{
				var update = await _updateService.CheckForUpdateAsync();
				if (update is null)
				{
					SetUpdateState(UpdateState.UpToDate);
					_ = Task.Delay(3000).ContinueWith(_ =>
					{
						if (_updateState == UpdateState.UpToDate)
							SetUpdateState(UpdateState.Idle);
					}, TaskScheduler.FromCurrentSynchronizationContext());
				}
				else
				{
					_availableUpdate = update;
					SetUpdateState(UpdateState.Available);
				}
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update check failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
		}

		private async Task PerformDownloadAsync()
		{
			if (_availableUpdate is null) return;
			SetUpdateState(UpdateState.Downloading);
			try
			{
				var progress = new Progress<double>(p =>
				{
					_downloadProgress = p;
					RaisePropertyChanged(nameof(UpdateMenuText));
				});

				_downloadedPath = await _updateService.DownloadUpdateAsync(_availableUpdate, progress);
				SetUpdateState(UpdateState.Ready);
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update download failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
		}

		private void PerformApplyAndRestart()
		{
			if (_downloadedPath is null) return;
			try
			{
				if (trayIcon.ContextMenu is { IsOpen: true } menu)
					menu.IsOpen = false;
				var snapshotPath = SceneSnapshot.Save(SceneManager.CreateSnapshot());
				UpdateService.ApplyUpdate(_downloadedPath);
				UpdateService.LaunchAndExit(snapshotPath);
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update apply failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
		}

		private void MenuItem_Quit_Click(object sender, RoutedEventArgs e)
		{
			Close();
		}

		private void ContextMenu_Closed(object sender, RoutedEventArgs e)
		{
			_trayMenuOpen = false;
		}

		private void ContextMenu_Opened(object sender, RoutedEventArgs e)
		{
			_trayMenuOpen = true;
		}
	}

	public enum WindowMode
	{
		OnScreen,
		OffScreen,
		Flyover
	}
}

