namespace Fotobox.Core;

/// <summary>
/// Bündelt schnelle Folgen von Aufträgen („nur der neueste zählt“), z. B. Schieberegler, die bei jeder Bewegung etwas an die Hardware
/// schreiben: Es läuft höchstens ein Auftrag gleichzeitig; kommen währenddessen weitere, bleibt nur der zuletzt eingereichte erhalten
/// und wird danach abgearbeitet (die dazwischenliegenden entfallen). UI-frei, wirft nie.
/// </summary>
/// <remarks>
/// Der von <see cref="SubmitAsync"/> gelieferte Task endet, sobald der eingereichte oder ein neuerer Auftrag abgearbeitet (auch mit
/// Fehler) oder verworfen ist (<see cref="Close"/>); er wirft nie. Die Arbeit läuft auf dem Threadpool, nie im Kontext des Aufrufers.
/// Fehler des Auftrags gehen an <c>onError</c> und beenden den Arbeiter nicht.
/// </remarks>
public sealed class LatestOnlyWorker<T>
{
    private readonly Func<T, Task> _work;
    private readonly Action<Exception>? _onError;
    private readonly object _lock = new();
    private readonly List<(long Seq, TaskCompletionSource Tcs)> _waiters = [];
    private T? _pending;
    private bool _hasPending;
    private long _pendingSeq;
    private long _nextSeq;
    private bool _running;
    private bool _closed;

    public LatestOnlyWorker(Func<T, Task> work, Action<Exception>? onError = null)
    {
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _onError = onError;
    }

    /// <summary>Reiht den Auftrag ein (ersetzt einen noch nicht begonnenen). Nach <see cref="Close"/> sofort abgeschlossen, ohne Arbeit.</summary>
    public Task SubmitAsync(T item)
    {
        TaskCompletionSource tcs;
        bool start;
        lock (_lock)
        {
            if (_closed)
                return Task.CompletedTask;

            var seq = ++_nextSeq;
            _pending = item;
            _hasPending = true;
            _pendingSeq = seq;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((seq, tcs));
            start = !_running;
            _running = true;
        }

        if (start)
            _ = Task.Run(RunLoopAsync);
        return tcs.Task;
    }

    /// <summary>
    /// Verwirft den wartenden Auftrag, schließt alle offenen Tasks ab und nimmt keine neuen mehr an. Ein gerade laufender Auftrag
    /// wird nicht unterbrochen, blockiert <see cref="Close"/> aber auch nicht.
    /// </summary>
    public void Close()
    {
        List<(long Seq, TaskCompletionSource Tcs)> waiters;
        lock (_lock)
        {
            _closed = true;
            _hasPending = false;
            _pending = default;
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var (_, tcs) in waiters)
            tcs.TrySetResult();
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            T item;
            long seq;
            lock (_lock)
            {
                if (!_hasPending || _closed)
                {
                    _running = false;
                    return;
                }

                item = _pending!;
                seq = _pendingSeq;
                _hasPending = false;
                _pending = default;
            }

            try
            {
                await _work(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { _onError?.Invoke(ex); }
                catch { /* Fehlerbehandlung darf den Arbeiter nie beenden. */ }
            }

            CompleteUpTo(seq);
        }
    }

    private void CompleteUpTo(long seq)
    {
        List<TaskCompletionSource> done = [];
        lock (_lock)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Seq <= seq)
                {
                    done.Add(_waiters[i].Tcs);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var tcs in done)
            tcs.TrySetResult();
    }
}
