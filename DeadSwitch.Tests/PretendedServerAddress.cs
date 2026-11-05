using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;

namespace DeadSwitch.Tests;

// Makes the in-memory test server claim that it listens on an address. No port is opened, so a test of
// the check can use an address on the network without anything leaving this machine.
internal sealed class PretendedServerAddress : IHostedService
{
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _address;
    private readonly TaskCompletionSource _stopping;

    public PretendedServerAddress(IServer server, IHostApplicationLifetime lifetime, string address, TaskCompletionSource stopping)
    {
        _server = server;
        _lifetime = lifetime;
        _address = address;
        _stopping = stopping;
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
