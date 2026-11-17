using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace FrameMotionPlus.Vulkan;

/// <summary>
/// A 16-byte device UUID as Vulkan (<c>VkPhysicalDeviceIDProperties.deviceUUID</c>) and CUDA (<c>cuDeviceGetUuid</c>)
/// report it. Bytes are kept and printed in their natural order, as <c>vulkaninfo</c> and <c>nvidia-smi</c> show them
/// (unlike <see cref="Guid"/>, which reorders the first three groups).
/// </summary>
public readonly struct DeviceUuid : IEquatable<DeviceUuid>
{
    public const int Size = 16;

    private readonly Bytes _bytes;

    private DeviceUuid(in Bytes bytes) => _bytes = bytes;

    public bool IsEmpty
    {
        get
        {
            foreach (var b in (ReadOnlySpan<byte>)_bytes)
            {
                if (b != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static DeviceUuid FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A device UUID is {Size} bytes, got {bytes.Length}.", nameof(bytes));
        }

        Bytes b = default;
        bytes.CopyTo(b);
        return new DeviceUuid(b);
    }

    /// <summary>Parses "624c61e8-563c-9d37-8e16-60031217e270", with or without dashes or a "GPU-" prefix.</summary>
    public static DeviceUuid Parse(string text)
    {
        if (!TryParse(text, out var uuid))
        {
            throw new FormatException($"'{text}' is not a 16-byte device UUID.");
        }

        return uuid;
    }

    public static bool TryParse([NotNullWhen(true)] string? text, out DeviceUuid uuid)
    {
        uuid = default;
        if (text is null)
        {
            return false;
        }

        var s = text.AsSpan().Trim();
        if (s.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase))
        {
            s = s[4..];
        }

        Bytes b = default;
        Span<byte> span = b;
        var count = 0;
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '-')
            {
                i++;
                continue;
            }

            if (count == Size || i + 1 >= s.Length
                || !byte.TryParse(s.Slice(i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            span[count++] = value;
            i += 2;
        }

        if (count != Size)
        {
            return false;
        }

        uuid = new DeviceUuid(b);
        return true;
    }

    public void CopyTo(Span<byte> destination) => ((ReadOnlySpan<byte>)_bytes).CopyTo(destination);

    public byte[] ToArray() => ((ReadOnlySpan<byte>)_bytes).ToArray();

    public bool Equals(DeviceUuid other) => ((ReadOnlySpan<byte>)_bytes).SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is DeviceUuid other && Equals(other);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        ReadOnlySpan<byte> b = _bytes;
        return string.Create(CultureInfo.InvariantCulture,
            $"{Convert.ToHexStringLower(b[..4])}-{Convert.ToHexStringLower(b[4..6])}-{Convert.ToHexStringLower(b[6..8])}-{Convert.ToHexStringLower(b[8..10])}-{Convert.ToHexStringLower(b[10..])}");
    }

    public static bool operator ==(DeviceUuid left, DeviceUuid right) => left.Equals(right);

    public static bool operator !=(DeviceUuid left, DeviceUuid right) => !left.Equals(right);

    [InlineArray(Size)]
    private struct Bytes
    {
        private byte _element0;
    }
}
