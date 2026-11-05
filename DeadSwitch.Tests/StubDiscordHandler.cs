using System.Net;

namespace DeadSwitch.Tests;

// Answers instead of Discord and remembers what it was sent, so no test ever reaches the network.
internal sealed class StubDiscordHandler : HttpMessageHandler
{
    // A field and not a property, because Interlocked needs a reference to the variable.
    public int Calls;

    public HttpStatusCode StatusToReturn = HttpStatusCode.OK;

    // When set, the handler throws this one instead of answering.
    public Exception? ExceptionToThrow;

    // When true, the handler never answers and only stops when the request is cancelled.
    public bool HangUntilCancelled;

    public HttpMethod? LastMethod;
    public Uri? LastUrl;
    public string LastBody = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        LastMethod = request.Method;
        LastUrl = request.RequestUri;

        if (request.Content != null)
        {
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        if (HangUntilCancelled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return new HttpResponseMessage(StatusToReturn);
    }
}
