using System;

namespace TopDock.Models
{
    public class SkipSegment
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string Category { get; set; } = string.Empty;

        public TimeSpan Duration => EndTime - StartTime;
    }
}
