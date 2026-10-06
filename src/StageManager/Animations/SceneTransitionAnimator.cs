using System;
using System.Windows;
using StageManager.Helpers;

namespace StageManager.Animations;
// Current switches restore directly. This host exists only for a card-sized drag fallback.
internal sealed class SceneTransitionAnimator : IDisposable
{
    private TransitionOverlayWindow? _overlay;
    public bool IsAnimating => false;
    internal TransitionOverlayWindow? Overlay => _overlay;
    internal TransitionOverlayWindow GetOrCreateOverlay(Rect cardBounds)
    {
        _overlay ??= new TransitionOverlayWindow();
        _overlay.PositionFrom(cardBounds);
        return _overlay;
    }
    internal void MoveDragOverlay(Rect cardBounds) => _overlay?.PositionFrom(cardBounds);
    internal void HideDragOverlay() => _overlay?.Hide();
    public void Dispose() { _overlay?.Close(); _overlay = null; }
}
