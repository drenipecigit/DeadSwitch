using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DeadSwitch.Api.Security;

// Security-critical: there is no login until phase 6, so the API must never be reachable from the
// network. launchSettings.json only sets the address for dotnet run and Visual Studio, and --urls or an
// environment variable can change it. This check therefore reads what the server really listens on.
//
// It runs after the server has started, so a wrong port is open for a moment before the application
// stops. Reading the settings first was rejected: an address can come from several places, and one
// that was forgotten would pass in silence.
public static class LocalOnlyCheck
{
    public static bool IsLocalAddress(string address)
    {
        // http://*:5201 and http://+:5201 are not valid addresses, so they stop here and count as not
        // local, which is the right answer: both mean every address on the machine.
        Uri? uri;
        if (!Uri.TryCreate(address, UriKind.Absolute, out uri))
        {
            return false;
        }

        return uri.IsLoopback;
    }

    public static void StopIfListeningBeyondLoopback(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature? listening = server.Features.Get<IServerAddressesFeature>();

        // The in-memory test server listens nowhere and has no list of addresses.
        if (listening == null)
        {
            return;
        }

        foreach (string address in listening.Addresses)
        {
            if (!IsLocalAddress(address))
            {
                app.Logger.LogCritical("The application has no login and must only listen on this machine, but it listens on {Address}. It is stopping.", address);
                Environment.ExitCode = 1;
                app.Lifetime.StopApplication();
                return;
            }
        }
    }
}
