#nullable enable
namespace T3.Editor.Gui.Windows.RenderExport;

internal static class RenderSettings
{
    public struct Settings
    {
        public TimeReference Reference;
        public float StartInBars;
        public float EndInBars;
        public float Fps;
        public int OverrideMotionBlurSamples;   // forwarded for operators that might read it
    }
    
    internal enum RenderMode
    {
        Video,
        ImageSequence
    }

    internal enum TimeReference
    {
        Bars,
        Seconds,
        Frames
    }

    internal enum TimeRanges
    {
        Custom,
        Loop,
        Soundtrack,
    }

    internal readonly struct QualityLevel
    {
        internal QualityLevel(double bits, string title, string description)
        {
            MinBitsPerPixelSecond = bits;
            Title = title;
            Description = description;
        }

        internal readonly double MinBitsPerPixelSecond;
        internal readonly string Title;
        internal readonly string Description;
    }
}