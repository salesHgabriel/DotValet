using System.Net;
using System.Net.Sockets;

namespace DotValet.Services;

public static class PortAllocator
{
    public const int FirstPort = 5100;

    public static int Next(IEnumerable<int> reserved)
    {
        var taken = reserved.ToHashSet();
        for (var port = FirstPort; port < 65000; port++)
        {
            if (!taken.Contains(port) && IsFree(port))
                return port;
        }
        throw new InvalidOperationException("Nenhuma porta livre encontrada.");
    }

    public static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
