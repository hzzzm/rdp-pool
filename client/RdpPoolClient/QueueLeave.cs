namespace RdpPoolClient;

/// <summary>
/// Best-effort leave-queue for exit / kill / tray paths.
/// Safe to call multiple times; 404 is ignored.
/// </summary>
static class QueueLeave
{
    static readonly object Gate = new();
    static HttpClient? _http;
    static volatile bool _inQueue;

    public static void Bind(HttpClient http) => _http = http;

    public static void MarkQueued(bool queued) => _inQueue = queued;

    public static bool IsQueued => _inQueue;

    /// <summary>Synchronous DELETE /api/v1/queue/me when still marked queued.</summary>
    public static void TryLeaveSync(string reason = "")
    {
        if (!_inQueue) return;
        HttpClient? http;
        lock (Gate)
        {
            http = _http;
            if (http == null) return;
            _inQueue = false;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Delete, "api/v1/queue/me");
            // Ensure auth header is present (HttpClient may already have DefaultRequestHeaders).
            var task = http.SendAsync(req, cts.Token);
            task.Wait(cts.Token);
            _ = task.Result;
            if (!string.IsNullOrEmpty(reason))
                Console.WriteLine("leave-queue: " + reason);
        }
        catch (Exception ex)
        {
            Console.WriteLine("leave-queue failed: " + ex.Message);
        }
    }

    public static async Task TryLeaveAsync(HttpClient http)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await http.DeleteAsync("api/v1/queue/me", cts.Token).ConfigureAwait(false);
            _inQueue = false;
            _ = resp;
        }
        catch
        {
            /* ignore */
        }
    }
}
