using AolToc.Protocol;

namespace AolToc.Server;

public sealed class InMemoryUserStore(bool allowRegistration)
{
  private readonly Dictionary<string, UserRecord> _users = new(StringComparer.OrdinalIgnoreCase);
  private readonly object _sync = new();

  public void AddOrUpdate(string screenName, string password)
  {
    if (!ScreenName.IsValid(screenName))
      throw new ArgumentException($"Invalid screen name '{screenName}'.", nameof(screenName));

    lock (_sync)
      _users[ScreenName.Normalize(screenName)] = new UserRecord(screenName, password);
  }

  public bool TryAuthenticate(string screenName, string password, out string displayName)
  {
    displayName = screenName;
    var normalized = ScreenName.Normalize(screenName);

    lock (_sync)
    {
      if (_users.TryGetValue(normalized, out var user))
      {
        displayName = user.ScreenName;
        return string.Equals(user.Password, password, StringComparison.Ordinal);
      }

      if (!allowRegistration)
        return false;

      _users[normalized] = new UserRecord(screenName, password);
      displayName = screenName;
      return true;
    }
  }

  private sealed record UserRecord(string ScreenName, string Password);
}
