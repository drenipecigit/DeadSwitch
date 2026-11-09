namespace DeadSwitch.Tests;

// Stands in for the network, so the probe can be tested with https addresses without any traffic.
// It counts the requests, because a check must never send more than one.
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    public int Requests;
    public HttpResponseMessage? Response;
    public Exception? Failure;
    public bool NeverAnswers;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;

        if (NeverAnswers)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Failure != null)
        {
            throw Failure;
        }

        return Response!;
    }
}
