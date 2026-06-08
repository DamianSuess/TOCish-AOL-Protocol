using System;
using System.Linq;
using System.Text;

namespace AolToc.Protocol;

public static class ScreenName
{
  public static string Normalize(string value)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(value);

    var builder = new StringBuilder(value.Length);
    foreach (var ch in value)
    {
      if (!char.IsWhiteSpace(ch))
      {
        builder.Append(char.ToLowerInvariant(ch));
      }
    }

    return builder.ToString();
  }

  public static bool IsValid(string value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return false;
    }

    var normalized = Normalize(value);
    if (normalized.Length is < 3 or > 32)
    {
      return false;
    }

    return normalized.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');
  }
}
