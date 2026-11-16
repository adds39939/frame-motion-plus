using System.Globalization;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.App.Cli;

/// <summary>Thrown for invalid command-line input; the message is shown to the user.</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }

    public UsageException()
    {
    }

    public UsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Minimal option parser: <c>--name value</c>, <c>--name=value</c> and boolean <c>--flag</c>. Every option
/// must be consumed, so typos fail loudly instead of being ignored.
/// </summary>
internal sealed class ArgumentReader
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    public ArgumentReader(IReadOnlyList<string> args, IReadOnlySet<string> flags)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3)
            {
                throw new UsageException($"Unexpected argument '{arg}'.");
            }

            string name;
            string? value;
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                name = arg[2..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                name = arg[2..];
                if (flags.Contains(name))
                {
                    value = null;
                }
                else
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new UsageException($"Option '--{name}' needs a value.");
                    }

                    value = args[++i];
                }
            }

            if (!_values.TryAdd(name, value))
            {
                throw new UsageException($"Option '--{name}' given more than once.");
            }
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public bool Flag(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return false;
        }

        _consumed.Add(name);
        return value is null || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    public string? String(string name)
    {
        if (!_values.TryGetValue(name, out var value))
        {
            return null;
        }

        _consumed.Add(name);
        return string.IsNullOrEmpty(value) ? throw new UsageException($"Option '--{name}' needs a value.") : value;
    }

    public double Double(string name, double fallback, double min, double max)
    {
        var text = String(name);
        if (text is null)
        {
            return fallback;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new UsageException($"Option '--{name}' expects a number, got '{text}'.");
        }

        if (value < min || value > max)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"Option '--{name}' must be between {min} and {max}."));
        }

        return value;
    }

    public long Int64(string name, long fallback, long min, long max)
    {
        var text = String(name);
        if (text is null)
        {
            return fallback;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new UsageException($"Option '--{name}' expects an integer, got '{text}'.");
        }

        if (value < min || value > max)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"Option '--{name}' must be between {min} and {max}."));
        }

        return value;
    }

    public FrameRate Rate(string name, FrameRate fallback)
    {
        var text = String(name);
        if (text is null)
        {
            return fallback;
        }

        if (!FrameRate.TryParse(text, out var rate) || rate.Hz > 1000)
        {
            throw new UsageException($"Option '--{name}' expects a rate such as 60, 59.94 or 60000/1001, got '{text}'.");
        }

        return rate;
    }

    public void EnsureAllConsumed()
    {
        foreach (var name in _values.Keys)
        {
            if (!_consumed.Contains(name))
            {
                throw new UsageException($"Unknown option '--{name}'.");
            }
        }
    }
}
