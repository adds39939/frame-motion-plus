namespace FrameMotionPlus.Sources.File;

/// <summary>FFmpeg 9's libraries are not there, or not of the version this build was checked against.</summary>
public sealed class FFmpegUnavailableException : Exception
{
    public FFmpegUnavailableException()
    {
    }

    public FFmpegUnavailableException(string message)
        : base(message)
    {
    }

    public FFmpegUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
