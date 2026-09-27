using System.Collections.ObjectModel;
using System.Windows.Threading;
using Nightrunner.Core.Logging;

namespace Nightrunner.UI;

/// <summary>What a running job can report: a fraction (0..1, or null when unknown) and a short line.</summary>
public sealed class JobContext(Job job, CancellationToken ct)
{
    public CancellationToken Token { get; } = ct;

    public void Report(double? fraction, string text) => job.Update(fraction, text);

    /// <summary>Report step <paramref name="done"/> of <paramref name="total"/>.</summary>
    public void Step(int done, int total, string text) => job.Update(total > 0 ? (double)done / total : null, text);
}

/// <summary>One queued piece of work. Bindable: the status bar shows the running one.</summary>
public sealed class Job : System.ComponentModel.INotifyPropertyChanged
{
    private readonly Dispatcher _ui;
    internal readonly CancellationTokenSource Cts = new();

    internal Job(string title, string source, Func<JobContext, string?> work, Dispatcher ui, bool logs)
    {
        Logs = logs;
        Title = title;
        Source = source;
        Work = work;
        _ui = ui;
    }

    public string Title { get; }
    public string Source { get; }
    /// <summary>False when the work logs its own operation (the raw exporter does), so the queue does not repeat it.</summary>
    internal bool Logs { get; }
    internal Func<JobContext, string?> Work { get; }
    public double? Fraction { get; private set; }
    public string Text { get; private set; } = "queued";
    public bool Cancelled => Cts.IsCancellationRequested;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void Cancel() => Cts.Cancel();

    internal void Update(double? fraction, string text) => _ui.BeginInvoke(() =>
    {
        Fraction = fraction;
        Text = text;
        PropertyChanged?.Invoke(this, new(nameof(Fraction)));
        PropertyChanged?.Invoke(this, new(nameof(Text)));
    });
}

/// <summary>
/// The app's one job queue: exports (and anything else long that writes files) run here, one at a time, off the UI
/// thread, each logged (start, result or failure, duration) and cancellable from the status bar.
/// </summary>
public static class Jobs
{
    private static readonly Queue<Job> Pending = new();
    private static readonly Lock Gate = new();
    private static bool _running;

    /// <summary>Every job not finished yet, the running one first. UI thread only.</summary>
    public static ObservableCollection<Job> Active { get; } = [];

    /// <summary>The job doing work now, readable from any thread (crash reports).</summary>
    public static Job? Running { get; private set; }

    /// <summary>Raised on the UI thread when a job ends: (job, summary or null, error or null).</summary>
    public static event Action<Job, string?, string?>? Finished;

    /// <summary>
    /// Queue <paramref name="work"/>. It returns a one-line summary for the log, throws to fail, and should check
    /// <see cref="JobContext.Token"/> between files.
    /// </summary>
    public static Job Run(string title, string source, Func<JobContext, string?> work, bool log = true)
    {
        var ui = System.Windows.Application.Current.Dispatcher;
        var job = new Job(title, source, work, ui, log);
        Active.Add(job);
        lock (Gate)
        {
            Pending.Enqueue(job);
            if (_running) return job;
            _running = true;
        }
        _ = Task.Run(Pump);
        return job;
    }

    /// <summary>Byte progress from the raw exporter, as a fraction.</summary>
    public static IProgress<Nightrunner.Core.Export.ExportProgress> Bytes(JobContext ctx, string what) =>
        new Relay(p => ctx.Report(p.TotalBytes > 0 ? (double)p.Bytes / p.TotalBytes : null,
                                  p.TotalBytes > 0 ? $"{Format.Size(p.Bytes)} of {Format.Size(p.TotalBytes)}" : what));

    private sealed class Relay(Action<Nightrunner.Core.Export.ExportProgress> report) : IProgress<Nightrunner.Core.Export.ExportProgress>
    {
        public void Report(Nightrunner.Core.Export.ExportProgress value) => report(value);
    }

    public static void CancelAll()
    {
        foreach (var j in Active.ToList()) j.Cancel();
    }

    private static void Pump()
    {
        while (true)
        {
            Job job;
            lock (Gate)
            {
                if (!Pending.TryDequeue(out job!))
                {
                    _running = false;
                    return;
                }
            }
            string? summary = null, error = null;
            Running = job;
            using (var op = job.Logs ? Log.Start(job.Source, job.Title) : Log.Operation.None)
            {
                if (job.Cancelled)
                {
                    op.Result = "cancelled before it started";
                    op.Level = LogLevel.Warn;
                }
                else
                {
                    try
                    {
                        job.Update(null, "running");
                        summary = job.Work(new JobContext(job, job.Cts.Token));
                        op.Result = job.Cancelled ? $"cancelled; {summary}" : summary ?? "done";
                        if (job.Cancelled) op.Level = LogLevel.Warn;
                    }
                    catch (OperationCanceledException)
                    {
                        op.Result = "cancelled";
                        op.Level = LogLevel.Warn;
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                        op.Failed(e.Message);
                    }
                }
            }
            Running = null;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                Active.Remove(job);
                Finished?.Invoke(job, summary, error);
            });
        }
    }
}
