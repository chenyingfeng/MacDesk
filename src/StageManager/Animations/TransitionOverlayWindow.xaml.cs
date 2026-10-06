using System.Windows.Controls;
using StageManager.Controls;

namespace StageManager.Animations
{
	public partial class TransitionOverlayWindow : LayeredOverlayWindowBase
	{
		public TransitionOverlayWindow()
		{
			InitializeComponent();
            IsHitTestVisible = false;
            SourceInitialized += (_, _) => {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                StageManager.Native.PInvoke.Win32.SetWindowStyleLongPtr(h,
                    StageManager.Native.PInvoke.Win32.GetWindowStyleLongPtr(h) | (StageManager.Native.PInvoke.Win32.WS)0x08000000);
            };
		}

		public Canvas Canvas => AnimationCanvas;
	}
}
