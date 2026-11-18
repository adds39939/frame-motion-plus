using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FrameMotionPlus.Audio.PipeWire;

namespace FrameMotionPlus.Audio;

/// <summary>An audio source or sink in the PipeWire graph; <paramref name="AlsaCard"/> is its ALSA card's id or number, if it is one.</summary>
public sealed record AudioNodeInfo(string Name, string? Description, bool IsDefault, string? AlsaCard);

/// <summary>
/// The audio sources and sinks PipeWire has now. A sink's <see cref="AudioNodeInfo.IsDefault"/> marks the default output
/// the user selected (<paramref name="SelectedSink"/>: default.configured.audio.sink, else WirePlumber's current default);
/// <paramref name="DefaultSink"/> is WirePlumber's current default, which differs while the selected one is unavailable.
/// </summary>
public sealed record AudioGraphSnapshot(IReadOnlyList<AudioNodeInfo> Sources, IReadOnlyList<AudioNodeInfo> Sinks, string? DefaultSink, string? DefaultSource, string LibraryVersion)
{
    public string? SelectedSink { get; init; }
}

/// <summary>
/// Finding audio devices: what PipeWire offers, and which audio source belongs to a V4L2 capture device (the sound card
/// that sits on the same USB device in sysfs, then the PipeWire source of that ALSA card).
/// </summary>
public static partial class AudioDevices
{
    /// <summary>Whether audio can work in this process (Linux, a libpipewire with every symbol used); false with the reason. No server is contacted.</summary>
    public static bool IsAvailable([NotNullWhen(false)] out string? reason) => PwLibrary.IsAvailable(out reason);

    /// <summary>The graph's audio sources and sinks; false with one line when there is no PipeWire to ask.</summary>
    public static bool TrySnapshot([NotNullWhen(true)] out AudioGraphSnapshot? snapshot, [NotNullWhen(false)] out string? reason)
    {
        snapshot = null;
        if (!PwConnection.TryConnect("fmp-list", out var connection, out reason))
        {
            return false;
        }

        using (connection)
        {
            List<PwNode> nodes;
            string? defaultSink, defaultSource, selected;
            using (connection.Lock())
            {
                nodes = connection.NodesLocked();
                defaultSink = connection.DefaultSink;
                defaultSource = connection.DefaultSource;
                selected = connection.DefaultConfiguredSink is { } c && connection.FindNodeLocked(c) is { IsSink: true } ? c : defaultSink;
            }

            var sources = nodes.Where(n => n.IsSource && !n.IsSink).Select(n => new AudioNodeInfo(n.Name, n.Description, n.Name == defaultSource, n.AlsaCard)).ToList();
            var sinks = nodes.Where(n => n.IsSink).Select(n => new AudioNodeInfo(n.Name, n.Description, n.Name == selected, n.AlsaCard)).ToList();
            snapshot = new AudioGraphSnapshot(sources, sinks, defaultSink, defaultSource, PwLibrary.Version ?? "?") { SelectedSink = selected };
            return true;
        }
    }

    /// <summary>
    /// The PipeWire source of the sound card on the same USB device as <paramref name="videoNode"/> (/dev/videoN), or null
    /// (no such card: vivid, a card without audio, not Linux).
    /// </summary>
    public static AudioNodeInfo? SourceForVideoDevice(AudioGraphSnapshot snapshot, string videoNode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var card = OperatingSystem.IsLinux() ? AlsaCardForVideoDevice(videoNode) : -1;
        if (card < 0)
        {
            return null;
        }

        // The node's object.path names the card by its id (/sys/class/sound/cardN/id) or by its number.
        var number = card.ToString(CultureInfo.InvariantCulture);
        string? id = null;
        try
        {
            id = File.ReadAllText($"/sys/class/sound/card{number}/id").Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return snapshot.Sources.Where(s => s.AlsaCard is { } c && (c == number || c == id))
            .OrderBy(s => s.Name.StartsWith("alsa_input.", StringComparison.Ordinal) ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>
    /// The ALSA card number of the sound device on the same USB device as the V4L2 node (sysfs: the parent of the video
    /// interface's device directory equals the parent of the sound card's), or -1.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static int AlsaCardForVideoDevice(string videoNode)
    {
        ArgumentNullException.ThrowIfNull(videoNode);
        var name = Path.GetFileName(videoNode);
        var videoDevice = RealPath($"/sys/class/video4linux/{name}/device");
        if (videoDevice is null || Path.GetDirectoryName(videoDevice) is not { } usbDevice || !usbDevice.Contains("/usb", StringComparison.Ordinal))
        {
            return -1;
        }

        if (!Directory.Exists("/sys/class/sound"))
        {
            return -1;
        }

        foreach (var entry in Directory.GetDirectories("/sys/class/sound", "card*"))
        {
            if (!int.TryParse(Path.GetFileName(entry).AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var card))
            {
                continue;
            }

            var soundDevice = RealPath(Path.Combine(entry, "device"));
            if (soundDevice is not null && Path.GetDirectoryName(soundDevice) == usbDevice)
            {
                return card;
            }
        }

        return -1;
    }

    [SupportedOSPlatform("linux")]
    private static unsafe string? RealPath(string path)
    {
        var resolved = RealPathNative(path, 0);
        if (resolved == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint RealPathNative(string path, nint resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(nint pointer);
}
