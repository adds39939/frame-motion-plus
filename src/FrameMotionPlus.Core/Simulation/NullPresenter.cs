using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Core.Simulation;

/// <summary>
/// A presenter with no display. It accepts every request and reports it as shown exactly at its target time.
/// </summary>
public sealed class NullPresenter : IPresenter
{
    private readonly SpscRing<PresentFeedback> _feedback = new(256);

    public NullPresenter(FrameRate refreshRate, long vblankAnchorNs = 0, bool vrr = false)
    {
        Timing = new DisplayTiming
        {
            RefreshRate = refreshRate,
            VblankAnchorNs = vblankAnchorNs,
            VrrActive = vrr,
            VrrMinimumRate = vrr ? FrameRate.FromHz(48) : default,
        };
        Capabilities = new PresenterCapabilities
        {
            Features = PresenterFeatures.PresentTiming | (vrr ? PresenterFeatures.Vrr : PresenterFeatures.None),
            MaxFramesInFlight = 2,
            Domain = MemoryDomain.Vulkan,
        };
    }

    public PresenterCapabilities Capabilities { get; }

    public DisplayTiming Timing { get; }

    public PresenterConfig Config { get; private set; }

    public long PresentCount { get; private set; }

    public long FeedbackDropped { get; private set; }

    public PresentRequest LastRequest { get; private set; }

    public void Configure(in PresenterConfig config) => Config = config;

    public bool TryPresent(in PresentRequest request)
    {
        LastRequest = request;
        PresentCount++;
        var feedback = new PresentFeedback
        {
            SlotIndex = request.SlotIndex,
            TargetTimeNs = request.TargetTimeNs,
            ActualTimeNs = request.TargetTimeNs,
        };
        if (!_feedback.TryEnqueue(feedback))
        {
            FeedbackDropped++;
        }

        return true;
    }

    /// <summary>Nothing to release without a display; only counted.</summary>
    public void Release(in PresentRequest request) => ReleaseCount++;

    public long ReleaseCount { get; private set; }

    public bool TryGetFeedback(out PresentFeedback feedback) => _feedback.TryDequeue(out feedback);

    public void Dispose()
    {
    }
}
