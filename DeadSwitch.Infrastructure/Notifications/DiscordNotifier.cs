using System.Net.Http.Json;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadSwitch.Infrastructure.Notifications;

public sealed class DiscordNotifier : INotifier
{
    public const string TimedOutMessage = "Discord did not answer in time.";
    public const string UnreachableMessage = "Discord could not be reached.";

    private readonly HttpClient _client;
    private readonly IOptions<DiscordOptions> _options;
    private readonly ILogger<DiscordNotifier> _logger;

    // Short, because the worker checks services one at a time and waits for this.
    // Internal so that a test can shorten it without really waiting five seconds.
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public DiscordNotifier(HttpClient client, IOptions<DiscordOptions> options, ILogger<DiscordNotifier> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task NotifyStatusChangedAsync(StatusChange change)
    {
        string? webhookUrl = _options.Value.WebhookUrl;

        // No address means that notifications are turned off, which is not an error.
        if (string.IsNullOrEmpty(webhookUrl))
        {
            return;
        }

        using CancellationTokenSource timeout = new CancellationTokenSource(Timeout);

        // wait=true: by default Discord answers without an error even if the message was not saved,
        // so an alarm that was lost would look like a success.
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, webhookUrl + "?wait=true");
        request.Content = JsonContent.Create(DiscordMessage.Create(change));

        int statusCode;
        try
        {
            // ResponseHeadersRead: the status code is all that is needed, so the body is never read.
            using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                // Logged here and not by the caller, because only this class knows that a message was
                // really sent: without an address it returns above without sending anything.
                _logger.LogInformation("Discord notification sent for service {ServiceId}.", change.ServiceId);
                return;
            }

            statusCode = (int)response.StatusCode;
        }
        catch (OperationCanceledException)
        {
            // Security: the original exception is left out on purpose, because its text can hold the address.
            throw new NotificationFailedException(TimedOutMessage);
        }
        catch (HttpRequestException)
        {
            // Security: the same as above.
            throw new NotificationFailedException(UnreachableMessage);
        }

        throw new NotificationFailedException("Discord answered with status " + statusCode + ".");
    }
}
