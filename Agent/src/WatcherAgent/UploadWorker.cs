namespace WatcherAgent;

// One sender per process. It only reads atomically published .json files and
// removes each file after its own matching durable Collector acknowledgement.
internal sealed class UploadWorker : IDisposable
{
    private readonly CancellationTokenSource stopping;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task worker;

    internal UploadWorker(AgentConfig config, CancellationToken stoppingToken = default)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        worker = Task.Run(() => RunAsync(config, stopping.Token));
    }

    internal void Wake()
    {
        // Coalesce notifications; a burst of sampled events needs just one wake-up.
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RunAsync(AgentConfig config, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int delivered = await Program.SendQueuedEventsAsync(config, token);
                    if (delivered > 0)
                    {
                        Console.WriteLine($"Acknowledged: {delivered}");
                        // Start the next bounded pass immediately, even if collection
                        // is sleeping or a platform sampling command is slow.
                        continue;
                    }
                    await wake.WaitAsync(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Collector unavailable; events retained: {ex.Message}");
                    // Sampling notifications must not bypass failure backoff.
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        stopping.Cancel();
        worker.GetAwaiter().GetResult();
        wake.Dispose();
        stopping.Dispose();
    }
}
