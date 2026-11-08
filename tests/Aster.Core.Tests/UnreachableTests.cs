using System.Net;
using System.Net.Sockets;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>Why a typed server address couldn't be reached, in words that say what to check.</summary>
public sealed class UnreachableTests
{
    private static async Task<Exception> Failure(string address)
    {
        try
        {
            await new JellyfinClient(new Uri(address)).PublicSystemInfoAsync();
        }
        catch (Exception error)
        {
            return error;
        }
        throw new InvalidOperationException("It answered.");
    }

    [Fact]
    public async Task APortWithNothingOnItSaysNothingAnswered()
    {
        // A port just freed: nothing listens there.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var error = await Failure($"http://127.0.0.1:{port}");
        Assert.Equal("Nothing answered at that address. Check it, and that the server is running.", JellyfinException.Unreachable(error));
    }

    [Fact]
    public async Task ANameThatDoesntExistSaysCheckForTypos()
    {
        // .invalid is never a real name (RFC 2606).
        var error = await Failure("http://no-such-server.invalid:8096");
        Assert.Equal("No server has that name. Check the address for typos.", JellyfinException.Unreachable(error));
    }

    [Fact]
    public void ACertificateNoNetworkAndATimeoutEachSayWhy()
    {
        Assert.Equal("The server's https:// certificate wasn't accepted. Try its http:// address.",
            JellyfinException.Unreachable(new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established")));
        Assert.Equal("This computer isn't connected to a network.",
            JellyfinException.Unreachable(new HttpRequestException(HttpRequestError.ConnectionError, "Network is unreachable", new SocketException((int)SocketError.NetworkUnreachable))));
        Assert.Equal("Nothing answered at that address. Check it, and that the server is running.",
            JellyfinException.Unreachable(new TimeoutException("The server took too long to answer.")));
        Assert.Equal("Incorrect user name or password.", JellyfinException.Unreachable(JellyfinException.Http(401, null)));
    }
}
