using System;
using System.Windows;

namespace StageManager.Composition;

// Native preview input goes to application gesture logic, not to a different HWND's WPF input provider.
internal sealed class PreviewPointerInput
{
    internal Func<Point,bool>? Down;
    internal Action<Point,bool>? Move;
    internal Action<Point>? Up;
    internal Action<int>? Wheel;
    internal Action? Cancel;
}
