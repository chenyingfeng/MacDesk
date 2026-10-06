using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using StageManager.Composition;

namespace StageManager.Controls;

// DWM-only thumbnail: no screen-capture session, GPU frame pool or capture permission.
public partial class CompositionThumbnail : UserControl
{
    public const double TrayTiltDegrees = 2.0;
    private static readonly HashSet<CompositionThumbnail> Live = new();
    private static bool shuttingDown;
    private DwmPreviewSurface? preview;

    public CompositionThumbnail()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => RefreshDwm();
        SizeChanged += (_, _) => RefreshDwm();
    }
    private static DependencyProperty Number(string name, double initial) =>
        DependencyProperty.Register(name, typeof(double), typeof(CompositionThumbnail), new PropertyMetadata(initial, Changed));
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CompositionThumbnail)d).RefreshDwm();
    public static readonly DependencyProperty PreviewHandleProperty = DependencyProperty.Register(nameof(PreviewHandle),
        typeof(IntPtr), typeof(CompositionThumbnail), new PropertyMetadata(IntPtr.Zero, Changed));
    public IntPtr PreviewHandle { get => (IntPtr)GetValue(PreviewHandleProperty); set => SetValue(PreviewHandleProperty, value); }
    public static readonly DependencyProperty CornerRadiusProperty = Number(nameof(CornerRadius), 8);
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public static readonly DependencyProperty TopEdgeDegreesProperty = Number(nameof(TopEdgeDegrees), 0);
    public double TopEdgeDegrees { get => (double)GetValue(TopEdgeDegreesProperty); set => SetValue(TopEdgeDegreesProperty, value); }
    public static readonly DependencyProperty BottomEdgeDegreesProperty = Number(nameof(BottomEdgeDegrees), 0);
    public double BottomEdgeDegrees { get => (double)GetValue(BottomEdgeDegreesProperty); set => SetValue(BottomEdgeDegreesProperty, value); }
    public static readonly DependencyProperty MirrorScaleProperty = Number(nameof(MirrorScale), 1);
    public double MirrorScale { get => (double)GetValue(MirrorScaleProperty); set => SetValue(MirrorScaleProperty, value); }
    public static readonly DependencyProperty MirrorOpacityProperty = Number(nameof(MirrorOpacity), 1);
    public double MirrorOpacity { get => (double)GetValue(MirrorOpacityProperty); set => SetValue(MirrorOpacityProperty, value); }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (shuttingDown) return;
        Live.Add(this);
        var owner = (MainWindow)Application.Current.MainWindow;
        preview ??= new DwmPreviewSurface(new WindowInteropHelper(owner).Handle,new PreviewPointerInput {
            Down=p=>owner.NativeSidebarPointerDown(p,FindSceneModel(),PreviewHandle),
            Move=owner.NativeSidebarPointerMove,Up=owner.NativeSidebarPointerUp,
            Wheel=owner.NativeSidebarWheel,Cancel=owner.NativeSidebarPointerCancel
        });
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
        RefreshDwm();
    }
    private StageManager.Model.SceneModel? FindSceneModel() {
        DependencyObject? current=this;
        while(current!=null) {
            if(current is FrameworkElement {DataContext:StageManager.Model.SceneModel scene})return scene;
            current=VisualTreeHelper.GetParent(current);
        }
        return null;
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Live.Remove(this);
        CompositionTarget.Rendering -= OnFrame;
        preview?.Dispose();
        preview = null;
    }
    private void OnFrame(object? sender, EventArgs e) => RefreshDwm();
    internal void RefreshDwm()
    {
        if (preview == null) return;
        var owner = Application.Current.MainWindow as MainWindow;
        if (!IsLoaded || !IsVisible || shuttingDown || owner == null || owner.Mode == WindowMode.OffScreen)
        {
            preview.Hide();
            return;
        }
        try
        {
            preview.Bind(PreviewHandle);
            var top = PointToScreen(new Point(0, 0));
            var bottom = PointToScreen(new Point(ActualWidth, ActualHeight));
            preview.Update(new Rect(top, bottom), owner.SidebarViewportPixels,
                (byte)(Math.Clamp(MirrorOpacity, 0, 1) * 255));
        }
        catch (Exception ex)
        {
            preview.Hide();
            Log.Info("DWMPREVIEW", "Keeping fallback card: " + ex.GetType().Name);
        }
    }
    internal static void ShutdownAll()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        foreach (var tile in Live)
        {
            CompositionTarget.Rendering -= tile.OnFrame;
            tile.preview?.Dispose();
            tile.preview = null;
        }
        Live.Clear();
    }
    internal static void ReleaseNativeCapture() {
        foreach(var tile in Live)tile.preview?.ReleaseInputCapture();
    }
}
