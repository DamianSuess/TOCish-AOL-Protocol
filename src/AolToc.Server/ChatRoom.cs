using AolToc.Protocol;

namespace AolToc.Server;

internal sealed class ChatRoom(string id, string name)
{
  private readonly Dictionary<string, ClientSession> _members = new(StringComparer.OrdinalIgnoreCase);
  private readonly object _sync = new();

  public string Id { get; } = id;
  public string Name { get; } = name;
  public string Key { get; } = ScreenName.Normalize(name);

  public bool Add(ClientSession session)
  {
    lock (_sync)
    {
      if (session.NormalizedScreenName is null)
      {
        return false;
      }

      return _members.TryAdd(session.NormalizedScreenName, session);
    }
  }

  public bool Remove(ClientSession session)
  {
    lock (_sync)
    {
      return session.NormalizedScreenName is not null && _members.Remove(session.NormalizedScreenName);
    }
  }

  public bool Contains(ClientSession session)
  {
    lock (_sync)
    {
      return session.NormalizedScreenName is not null && _members.ContainsKey(session.NormalizedScreenName);
    }
  }

  public ClientSession? FindMember(string screenName)
  {
    lock (_sync)
    {
      _members.TryGetValue(ScreenName.Normalize(screenName), out var session);
      return session;
    }
  }

  public IReadOnlyList<ClientSession> Snapshot()
  {
    lock (_sync)
    {
      return _members.Values.ToArray();
    }
  }
}