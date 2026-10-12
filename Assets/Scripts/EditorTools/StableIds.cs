using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// Deterministic IDs derived from names, so regenerating assets never rewrites .meta files or breaks
    /// references (sprite IDs, sorting-layer IDs). (GUID lives in UnityEngine as of Unity 6.4.)
    /// </summary>
    public static class StableIds
    {
        static byte[] Hash(string key)
        {
            using var md5 = MD5.Create();
            return md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        }

        public static GUID Guid(string key)
        {
            var sb = new StringBuilder(32);
            foreach (var b in Hash(key)) sb.Append(b.ToString("x2"));
            return new GUID(sb.ToString());
        }

        /// <summary>Positive 31-bit ID (sorting layers need a unique, non-zero uint stored as int).</summary>
        public static int PositiveInt(string key)
        {
            var h = Hash(key);
            int v = (h[0] | (h[1] << 8) | (h[2] << 16) | (h[3] << 24)) & 0x7fffffff;
            return v == 0 ? 1 : v;
        }
    }
}
