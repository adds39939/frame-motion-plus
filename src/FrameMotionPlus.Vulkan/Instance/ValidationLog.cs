using System.Text;

namespace FrameMotionPlus.Vulkan;

public enum ValidationSeverity
{
    Verbose = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

[Flags]
public enum ValidationMessageKinds
{
    None = 0,
    General = 1,
    Validation = 2,
    Performance = 4,
}

/// <summary>One message from the debug messenger (validation layer or loader).</summary>
public sealed record ValidationMessage(
    ValidationSeverity Severity,
    ValidationMessageKinds Kinds,
    int MessageId,
    string MessageIdName,
    string Text)
{
    /// <summary>
    /// A warning or error that is ours to fix. The loader's notices about the layers our own policy filtered out are
    /// expected and excluded; everything else from the loader or a layer at warning level or above counts.
    /// </summary>
    public bool IsProblem =>
        Severity >= ValidationSeverity.Warning
        && !(MessageIdName == "Loader Message" && VulkanLayerPolicy.IsFilterNotice(Text));

    public override string ToString() => $"[{Severity}] {MessageIdName} ({MessageId}): {Text}";
}

/// <summary>
/// Collects debug-messenger output for an instance. Thread-safe: the messenger calls back on whichever thread made
/// the Vulkan call. Collecting a message allocates; that only happens when something is reported.
/// </summary>
public sealed class ValidationLog
{
    private readonly Lock _lock = new();
    private readonly List<ValidationMessage> _messages = [];
    private int _problems;

    /// <summary>Called for every message after it is recorded (e.g. to echo to a log). Runs on the calling thread.</summary>
    public Action<ValidationMessage>? OnMessage { get; set; }

    public int ProblemCount => Volatile.Read(ref _problems);

    public IReadOnlyList<ValidationMessage> All()
    {
        lock (_lock)
        {
            return [.. _messages];
        }
    }

    public IReadOnlyList<ValidationMessage> Problems()
    {
        lock (_lock)
        {
            return _messages.FindAll(static m => m.IsProblem);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
            _problems = 0;
        }
    }

    public string Describe()
    {
        var problems = Problems();
        if (problems.Count == 0)
        {
            return "no validation problems";
        }

        var sb = new StringBuilder();
        sb.Append(problems.Count).Append(" validation problem(s):");
        foreach (var m in problems)
        {
            sb.AppendLine().Append("  ").Append(m);
        }

        return sb.ToString();
    }

    internal void Add(ValidationMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
            if (message.IsProblem)
            {
                _problems++;
            }
        }

        OnMessage?.Invoke(message);
    }
}
