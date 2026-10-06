using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StageManager.Services
{
    // Owned by one dispatcher. Run one operation at a time, coalesce pending choices,
    // and make a repeated click on the active target harmless. No synchronous waits.
    internal sealed class LatestRequestQueue<T>
    {
        private readonly Func<T, Task> _run;
        private readonly Action<Exception> _onError;
        private readonly IEqualityComparer<T> _comparer = EqualityComparer<T>.Default;
        private T _pending = default!;
        private T _active = default!;
        private T _lastCompleted = default!;
        private bool _hasPending, _hasCompleted;
        private long _completedAt;
        private Task _drain = Task.CompletedTask;
        internal bool IsBusy { get; private set; }

        internal LatestRequestQueue(Func<T, Task> run, Action<Exception> onError)
        { _run = run; _onError = onError; }

        internal Task RequestAsync(T value)
        {
            if (IsBusy && _comparer.Equals(value, _active)) return _drain;
            if (!IsBusy && _hasCompleted && _comparer.Equals(value, _lastCompleted)
                && Stopwatch.GetElapsedTime(_completedAt).TotalMilliseconds < 350)
                return Task.CompletedTask;
            _pending = value;
            _hasPending = true;
            if (IsBusy) return _drain;
            IsBusy = true;
            _active = value;
            _drain = DrainAsync();
            return _drain;
        }

        private async Task DrainAsync()
        {
            // Let the mouse-up dispatch unwind before any external app or WinRT call.
            await Task.Yield();
            try
            {
                while (_hasPending)
                {
                    _active = _pending;
                    _hasPending = false;
                    try
                    {
                        await _run(_active);
                        _lastCompleted = _active;
                        _hasCompleted = true;
                        _completedAt = Stopwatch.GetTimestamp();
                    }
                    catch (Exception ex) { _onError(ex); }
                }
            }
            finally { IsBusy = false; }
        }
    }
}
