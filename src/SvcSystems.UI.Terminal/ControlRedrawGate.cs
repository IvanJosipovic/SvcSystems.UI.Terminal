using Avalonia.Controls;
using Avalonia.Threading;
using System.Diagnostics;

namespace SvcSystems.UI.Terminal;

/// <summary>Paces visual invalidations for one terminal surface.</summary>
internal sealed class ControlRedrawGate
{
    private readonly object _requestLock = new();
    private readonly Control _surface;
    private readonly DispatcherTimer _timer;
    private Action? _queuedUiUpdate;
    private TimeSpan _minimumInterval;
    private long _lastRedrawTimestamp;
    private bool _hasQueuedUiUpdate;
    private bool _dispatcherWorkQueued;
    private bool _hasPendingInvalidation;
    private bool _suspended;

    internal ControlRedrawGate(Control surface, TimeSpan minimumInterval)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ValidateInterval(minimumInterval);

        _surface = surface;
        _minimumInterval = minimumInterval;
        _timer = new DispatcherTimer();
        _timer.Tick += OnTimerTick;
    }

    internal bool HasPendingInvalidationForTests => _hasPendingInvalidation;

    internal void Request(Action? updateUiState = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!_suspended)
            {
                updateUiState?.Invoke();
                ScheduleInvalidation();
            }

            return;
        }

        lock (_requestLock)
        {
            if (_suspended)
            {
                return;
            }

            _hasQueuedUiUpdate = true;
            _queuedUiUpdate = updateUiState ?? _queuedUiUpdate;
            if (_dispatcherWorkQueued)
            {
                return;
            }

            _dispatcherWorkQueued = true;
        }

        Dispatcher.UIThread.Post(ProcessQueuedUiUpdate);
    }

    internal void SetMinimumInterval(TimeSpan minimumInterval)
    {
        ValidateInterval(minimumInterval);
        _minimumInterval = minimumInterval;

        if (Dispatcher.UIThread.CheckAccess())
        {
            RecheckPendingInvalidation();
        }
        else
        {
            Dispatcher.UIThread.Post(RecheckPendingInvalidation);
        }
    }

    internal void Suspend()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Suspend);
            return;
        }

        lock (_requestLock)
        {
            _suspended = true;
            _hasQueuedUiUpdate = false;
            _queuedUiUpdate = null;
        }

        _hasPendingInvalidation = false;
        _timer.Stop();
    }

    internal void Resume()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Resume);
            return;
        }

        lock (_requestLock)
        {
            _suspended = false;
        }
    }

    private static void ValidateInterval(TimeSpan minimumInterval)
    {
        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval), minimumInterval,
                "The redraw interval cannot be negative.");
        }
    }

    private void ProcessQueuedUiUpdate()
    {
        Action? update;
        lock (_requestLock)
        {
            _dispatcherWorkQueued = false;
            if (_suspended)
            {
                _hasQueuedUiUpdate = false;
                _queuedUiUpdate = null;
                return;
            }

            if (!_hasQueuedUiUpdate)
            {
                return;
            }

            _hasQueuedUiUpdate = false;
            update = _queuedUiUpdate;
            _queuedUiUpdate = null;
        }

        update?.Invoke();
        ScheduleInvalidation();
    }

    private void RecheckPendingInvalidation()
    {
        if (!_hasPendingInvalidation || _suspended)
        {
            return;
        }

        _timer.Stop();
        ScheduleInvalidation();
    }

    private void ScheduleInvalidation()
    {
        if (_suspended)
        {
            return;
        }

        var elapsed = _lastRedrawTimestamp == 0
            ? _minimumInterval
            : Stopwatch.GetElapsedTime(_lastRedrawTimestamp);
        var delay = _minimumInterval - elapsed;
        if (delay <= TimeSpan.Zero)
        {
            _timer.Stop();
            _hasPendingInvalidation = false;
            Redraw();
            return;
        }

        _hasPendingInvalidation = true;
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_suspended || !_hasPendingInvalidation)
        {
            return;
        }

        _hasPendingInvalidation = false;
        Redraw();
    }

    private void Redraw()
    {
        if (_suspended)
        {
            return;
        }

        _lastRedrawTimestamp = Stopwatch.GetTimestamp();
        _surface.InvalidateVisual();
    }
}
