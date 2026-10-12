using UnityEngine;
using UnityEngine.UI;

namespace Loamlight.UI
{
    /// <summary>Keeps the canvas at an integer scale of a 640x360 virtual screen, so UI pixels stay square.</summary>
    [RequireComponent(typeof(CanvasScaler))]
    public sealed class IntegerUiScale : MonoBehaviour
    {
        public static int ExtraScale;
        CanvasScaler _scaler;

        void Awake() => _scaler = GetComponent<CanvasScaler>();

        void Update()
        {
            int s = Mathf.Max(1, Mathf.Min(Screen.width / 640, Screen.height / 360) + ExtraScale);
            if (!Mathf.Approximately(_scaler.scaleFactor, s)) _scaler.scaleFactor = s;
        }
    }
}
