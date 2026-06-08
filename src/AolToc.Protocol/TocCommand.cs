using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace AolToc.Protocol;

public sealed record TocCommand(string Name, IReadOnlyList<string> Arguments)
{
  public static TocCommand Parse(string payload)
  {
    ArgumentNullException.ThrowIfNull(payload);

    var text = payload.TrimEnd('\0', '\r', '\n');
    var tokens = Tokenize(text);
    if (tokens.Count == 0)
    {
      throw new FormatException("TOC command payload is empty.");
    }

    return new TocCommand(tokens[0], new ReadOnlyCollection<string>(tokens.Skip(1).ToArray()));
  }

  public static string Format(string name, params string[] arguments)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);

    if (arguments.Length == 0)
    {
      return name;
    }

    return string.Join(' ', new[] { name }.Concat(arguments.Select(Quote)));
  }

  public override string ToString()
  {
    return Format(Name, Arguments.ToArray());
  }

  private static List<string> Tokenize(string text)
  {
    var tokens = new List<string>();
    var current = new StringBuilder();
    var inQuotes = false;
    var escaping = false;
    var tokenStarted = false;

    foreach (var ch in text)
    {
      if (escaping)
      {
        current.Append(ch);
        escaping = false;
        tokenStarted = true;
        continue;
      }

      if (ch == '\\')
      {
        escaping = true;
        tokenStarted = true;
        continue;
      }

      if (ch == '"')
      {
        inQuotes = !inQuotes;
        tokenStarted = true;
        continue;
      }

      if (char.IsWhiteSpace(ch) && !inQuotes)
      {
        if (tokenStarted)
        {
          tokens.Add(current.ToString());
          current.Clear();
          tokenStarted = false;
        }

        continue;
      }

      current.Append(ch);
      tokenStarted = true;
    }

    if (escaping)
    {
      current.Append('\\');
      tokenStarted = true;
    }

    if (inQuotes)
    {
      throw new FormatException("TOC command contains an unterminated quoted argument.");
    }

    if (tokenStarted)
    {
      tokens.Add(current.ToString());
    }

    return tokens;
  }

  private static string Quote(string value)
  {
    if (value.Length > 0 && value.All(ch => !char.IsWhiteSpace(ch) && ch != '"' && ch != '\\'))
    {
      return value;
    }

    var builder = new StringBuilder(value.Length + 2);
    builder.Append('"');
    foreach (var ch in value)
    {
      if (ch is '"' or '\\')
      {
        builder.Append('\\');
      }

      builder.Append(ch);
    }

    builder.Append('"');
    return builder.ToString();
  }
}
