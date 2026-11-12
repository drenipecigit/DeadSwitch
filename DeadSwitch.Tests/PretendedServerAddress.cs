using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;

namespace DeadSwitch.Tests;

// Makes the in-memory test server claim that it listens on an address. No port is opened, so a test of
// the check can use an address on the network without anything leaving this machine. It also holds back
// the end of the application's stop until the test host has started.
internal sealed class PretendedServerAddress : IHostedService
{
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _address;
    private readonly TaskCompletionSource _stopping;
    private readonly TaskCompletionSource _testHostStarted;

    public PretendedServerAddress(IServer server, IHostApplicationLifetime lifetime, string address, TaskCompletionSource stopping, TaskCompletionSource testHostStarted)
    {
        _server = server;
        _lifetime = lifetime;
        _address = address;
        _stopping = stopping;
        _testHostStarted = testHostStarted;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ServerAddressesFeature feature = new ServerAddressesFeature();
        feature.Addresses.Add(_address);
        _server.Features.Set<IServerAddressesFeature>(feature);

        // Listened for here, because a test that asks afterwards can find the application already gone.
        _lifetime.ApplicationStopping.Register(_stopping.SetResult);

        return Task.CompletedTask;
    }

    // The application disposes its services right after this. If the check stops the application before
    // the test host has finished starting it, the test host would then read disposed services, and the
    // result would depend on which thread was faster. Waiting here gives one fixed order.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The limit is only a safety net against a hang. It is longer than the 10 seconds TestApp waits
        // for Program, so that a test which waits for Program fails on that limit first.
        await _testHostStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
