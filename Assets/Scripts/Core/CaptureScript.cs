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

    /// <summary>Optional: a capture director that can check each captured frame (e.g. compare regions).</summary>
    public interface ICaptureVerifier
    {
        /// <returns>false if the frame is wrong; <paramref name="detail"/> goes into the capture report either way.</returns>
        bool VerifyShot(string shotName, UnityEngine.Texture2D frame, out string detail);
    }

    public sealed class CaptureShot
    {
        public readonly string Name;
        public CaptureShot(string name) => Name = name;
    }
}
