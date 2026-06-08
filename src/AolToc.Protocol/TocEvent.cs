using System;
using System.Collections.Generic;
using System.Linq;

namespace AolToc.Protocol;

public sealed record TocEvent(string Name, IReadOnlyList<string> Arguments)
{
  public static TocEvent Parse(string payload)
  {
    ArgumentNullException.ThrowIfNull(payload);

    var text = payload.TrimEnd('\0', '\r', '\n');
    var parts = text.Split(':');
    if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
    {
      throw new FormatException("TOC event payload is empty.");
    }

    return new TocEvent(parts[0], parts.Skip(1).ToArray());
  }

  public string JoinArgumentsFrom(int index)
  {
    if (index >= Arguments.Count)
    {
      return string.Empty;
    }

    return string.Join(':', Arguments.Skip(index));
  }
}