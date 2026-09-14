using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PDFPlus.Core;

public sealed class RenderRequest
{
    public required PdfDocument Document { get; init; }
    public required int PageIndex { get; init; }
    public required int PageWidth { get; init; }
    public required int PageHeight { get; init; }
    public required Int32Rect Clip { get; init; }
    /// <summary>Lower runs first. 0 = visible page, 1 = nearby/hi-res tile, 2 = thumbnail.</summary>
    public int Priority { get; init; }
    /// <summary>Checked on the render thread right before rendering; must only read thread-safe state.</summary>
    public Func<bool>? IsStale { get; init; }
    /// <summary>Invoked on the UI thread. Receives null if the request was skipped or failed.</summary>
    public required Action<BitmapSource?> Completed { get; init; }
    internal long Sequence;
}

/// <summary>
/// One dedicated render thread shared by all documents. Newest requests of the best priority win,
/// so fast scrolling renders what is on screen now instead of working through a backlog.
/// </summary>
public static class RenderService
{
    private static readonly List<RenderRequest> Queue = new();
    private static readonly object Gate = new();
    private static Dispatcher? _ui;
    private static long _sequence;

    public static void Start(Dispatcher ui)
    {
        _ui = ui;
        var thread = new Thread(Run) { IsBackground = true, Name = "PDFPlus renderer" };
        thread.Start();
    }

    public static void Enqueue(RenderRequest request)
    {
        lock (Gate)
        {
            request.Sequence = ++_sequence;
            Queue.Add(request);
            Monitor.Pulse(Gate);
        }
    }

    public static void Cancel(PdfDocument document)
    {
        List<RenderRequest> removed;
        lock (Gate)
        {
            removed = Queue.Where(r => r.Document == document).ToList();
            Queue.RemoveAll(r => r.Document == document);
        }
        foreach (var r in removed) r.Completed(null);
    }

    private static void Run()
    {
        while (true)
        {
            RenderRequest next;
            lock (Gate)
            {
                while (Queue.Count == 0) Monitor.Wait(Gate);
                var best = 0;
                for (var i = 1; i < Queue.Count; i++)
                {
                    var candidate = Queue[i];
                    var current = Queue[best];
                    if (candidate.Priority < current.Priority ||
                        (candidate.Priority == current.Priority && candidate.Sequence > current.Sequence))
                        best = i;
                }
                next = Queue[best];
                Queue.RemoveAt(best);
            }

            BitmapSource? bitmap = null;
            try
            {
                if (next.IsStale?.Invoke() != true)
                    bitmap = next.Document.Render(next.PageIndex, next.PageWidth, next.PageHeight, next.Clip);
            }
            catch
            {
                bitmap = null;
            }

            var request = next;
            var result = bitmap;
            _ui!.BeginInvoke(DispatcherPriority.Render, () => request.Completed(result));
        }
    }
}
