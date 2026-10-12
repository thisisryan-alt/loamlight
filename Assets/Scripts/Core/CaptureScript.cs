using System.Collections;

namespace Loamlight.Core
{
    /// <summary>
    /// A scene component that knows how to stage itself for screenshot captures (the -llCapture debug flag).
    /// <see cref="CaptureScript"/> is a coroutine: yield normal Unity yield instructions to wait, and yield a
    /// <see cref="CaptureShot"/> to have the runner take a screenshot of the current frame.
    /// </summary>
    public interface ICaptureDirector
    {
        IEnumerator CaptureScript();
    }

    public sealed class CaptureShot
    {
        public readonly string Name;
        public CaptureShot(string name) => Name = name;
    }
}
