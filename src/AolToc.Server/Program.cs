using System.Net;
using AolToc.Protocol;

namespace AolToc.Server;

internal static class Program
{
  public static async Task<int> Main(string[] args)
  {
    var options = ServerOptions.Parse(args);
    var users = new InMemoryUserStore(options.AllowRegistration);

    if (options.Users.Count == 0)
    {
      users.AddOrUpdate("alice", "password");
      users.AddOrUpdate("bob", "password");
      users.AddOrUpdate("carol", "password");
    }
    else
    {
      foreach (var user in options.Users)
      {
        users.AddOrUpdate(user.Key, user.Value);
      }
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
      eventArgs.Cancel = true;
      cancellation.Cancel();
    };

    var server = new TocChatServer(options.Address, options.Port, users);
    Console.WriteLine($"AOL TOC chat server listening on {options.Address}:{options.Port}");
    Console.WriteLine("Press Ctrl+C to stop.");

    try
    {
      await server.RunAsync(cancellation.Token).ConfigureAwait(false);
      return 0;
    }
    catch (OperationCanceledException)
    {
      return 0;
    }
  }
}

internal sealed record ServerOptions(
    IPAddress Address,
    int Port,
    bool AllowRegistration,
    IReadOnlyDictionary<string, string> Users)
{
  public static ServerOptions Parse(string[] args)
  {
    var address = IPAddress.Loopback;
    var port = 5190;
    var allowRegistration = false;
    var users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < args.Length; i++)
    {
      switch (args[i])
      {
        case "--host" when i + 1 < args.Length:
          address = ParseAddress(args[++i]);
          break;

        case "--port" when i + 1 < args.Length:
          port = int.Parse(args[++i]);
          break;

        case "--allow-registration":
          allowRegistration = true;
          break;

        case "--user" when i + 1 < args.Length:
          AddUser(users, args[++i]);
          break;

        case "--help":
        case "-h":
          PrintHelp();
          Environment.Exit(0);
          break;

        default:
          throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
      }
    }

    return new ServerOptions(address, port, allowRegistration, users);
  }

  private static IPAddress ParseAddress(string value)
  {
    if (string.Equals(value, "any", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
    {
      return IPAddress.Any;
    }

    if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
    {
      return IPAddress.Loopback;
    }

    return IPAddress.Parse(value);
  }

  private static void AddUser(Dictionary<string, string> users, string assignment)
  {
    var separator = assignment.IndexOf('=');
    if (separator <= 0 || separator == assignment.Length - 1)
    {
      throw new ArgumentException("--user expects screenName=password.");
    }

    users[assignment[..separator]] = assignment[(separator + 1)..];
  }

  private static void PrintHelp()
  {
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/AolToc.Server -- [--host 127.0.0.1] [--port 5190]");
    Console.WriteLine("  dotnet run --project src/AolToc.Server -- --user alice=password --user bob=password");
    Console.WriteLine("  dotnet run --project src/AolToc.Server -- --allow-registration");
  }
}
