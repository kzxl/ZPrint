using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ZPrint.Core.Models;
using ZPrint.Core.Spooler;

namespace ZPrint.Core.Queue
{
    /// <summary>
    /// Thread-safe, FIFO print job coordinator that prevents page interleaving and manages background execution.
    /// </summary>
    public sealed class PrintJobQueue : IDisposable
    {
        private readonly IPrinterSpooler _spooler;
        private readonly ConcurrentQueue<PrintJob> _pendingQueue = new ConcurrentQueue<PrintJob>();
        private readonly ConcurrentDictionary<string, PrintJob> _allJobs = new ConcurrentDictionary<string, PrintJob>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private int _disposed;

        public event Action<PrintJob>? JobEnqueued;
        public event Action<PrintJob>? JobStarted;
        public event Action<PrintJob>? JobCompleted;
        public event Action<PrintJob, Exception>? JobFaulted;

        public int PendingCount => _pendingQueue.Count;
        public IReadOnlyCollection<PrintJob> AllJobs => _allJobs.Values.ToArray();

        public PrintJobQueue(IPrinterSpooler spooler)
        {
            _spooler = spooler ?? throw new ArgumentNullException(nameof(spooler));
            _workerTask = Task.Run(ProcessQueueLoopAsync);
        }

        public bool EnqueueJob(PrintJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (Volatile.Read(ref _disposed) != 0) return false;

            job.Status = JobStatus.Queued;
            _allJobs[job.JobId] = job;
            _pendingQueue.Enqueue(job);

            JobEnqueued?.Invoke(job);
            _signal.Release();
            return true;
        }

        public PrintJob? GetJob(string jobId)
        {
            _allJobs.TryGetValue(jobId, out var job);
            return job;
        }

        public bool CancelJob(string jobId)
        {
            if (_allJobs.TryGetValue(jobId, out var job))
            {
                if (job.Status == JobStatus.Queued)
                {
                    job.Status = JobStatus.Cancelled;
                    return true;
                }
            }
            return false;
        }

        private async Task ProcessQueueLoopAsync()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(token).ConfigureAwait(false);

                    if (!_pendingQueue.TryDequeue(out var job))
                        continue;

                    if (job.Status == JobStatus.Cancelled)
                        continue;

                    job.Status = JobStatus.Printing;
                    JobStarted?.Invoke(job);

                    try
                    {
                        for (int i = 0; i < Math.Max(1, job.Copies); i++)
                        {
                            string docTitle = $"{job.DocumentName} (Job {job.JobId})";
                            bool ok = _spooler.PrintRaw(job.TargetPrinter, docTitle, job.Data, "RAW");
                            if (!ok)
                            {
                                throw new InvalidOperationException($"Printer '{job.TargetPrinter}' failed to accept raw spool stream.");
                            }
                        }

                        job.Status = JobStatus.Completed;
                        JobCompleted?.Invoke(job);
                    }
                    catch (Exception ex)
                    {
                        job.Status = JobStatus.Faulted;
                        job.ErrorMessage = ex.Message;
                        JobFaulted?.Invoke(job, ex);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Prevent worker loop crash
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            try { _signal.Release(); } catch { }
            _cts.Dispose();
            _signal.Dispose();
        }
    }
}
