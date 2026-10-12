using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>Smooth follow with a little look-ahead, clamped so the camera never shows outside the world.</summary>
    public sealed class CameraFollow : MonoBehaviour
    {
        public PlayerController target;
        public Vector2 worldSize;
        public float smoothTime = 0.09f;
        public float lookAhead = 2.5f;
        public Vector2 offset = new Vector2(0f, 1.5f);

        Camera _cam;
        Vector3 _velocity;
        float _shake;
        float _lookX;

        void Awake() => _cam = GetComponent<Camera>();

        /// <summary>Adds camera trauma (0..1); shake falls off quadratically.</summary>
        public void AddTrauma(float amount) => _shake = Mathf.Clamp01(_shake + amount);

        public void SnapToTarget()
        {
            if (target == null) return;
            var p = Desired();
            transform.position = Clamp(new Vector3(p.x, p.y, transform.position.z));
            _velocity = Vector3.zero;
        }

        Vector2 Desired()
        {
            _lookX = Mathf.Lerp(_lookX, target.Velocity.x / 9.5f * lookAhead, Time.unscaledDeltaTime * 3f);
            return (Vector2)target.transform.position + offset + new Vector2(_lookX, 0f);
        }

        void LateUpdate()
        {
            if (target == null) return;
            var p = Desired();
            var pos = Vector3.SmoothDamp(transform.position, new Vector3(p.x, p.y, transform.position.z), ref _velocity, smoothTime,
                Mathf.Infinity, Time.unscaledDeltaTime);
            pos = Clamp(pos);
            if (_shake > 0f)
            {
                float s = _shake * _shake;
                float t = Time.unscaledTime * 40f;
                pos += new Vector3((Mathf.PerlinNoise(t, 0.3f) - 0.5f) * 1.2f * s, (Mathf.PerlinNoise(0.7f, t) - 0.5f) * 1.2f * s, 0f);
                _shake = Mathf.Max(0f, _shake - Time.unscaledDeltaTime * 1.8f);
            }
            transform.position = pos;
        }

        Vector3 Clamp(Vector3 p)
        {
            if (_cam == null || worldSize == Vector2.zero) return p;
            float halfH = _cam.orthographicSize, halfW = halfH * _cam.aspect;
            p.x = worldSize.x > halfW * 2 ? Mathf.Clamp(p.x, halfW, worldSize.x - halfW) : worldSize.x * 0.5f;
            p.y = worldSize.y > halfH * 2 ? Mathf.Clamp(p.y, halfH, worldSize.y - halfH) : worldSize.y * 0.5f;
            return p;
        }
    }
}
