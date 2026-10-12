using System;

namespace Loamlight.Core
{
    /// <summary>Command-line flags for debug launches, e.g. <c>Loamlight.exe -llCapture out -llScene PipelineDemo</c>.</summary>
    public static class LaunchArgs
    {
        static string[] _args;

        static string[] Args => _args ??= Environment.GetCommandLineArgs();

        /// <summary>Test hook: replace the argument list (null restores the real command line).</summary>
        public static void Override(string[] args) => _args = args;

        public static bool Has(string flag)
        {
            foreach (var a in Args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool TryGet(string flag, out string value)
        {
            var args = Args;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    value = args[i + 1].Trim('"');
                    return true;
                }
            }
            value = null;
            return false;
        }

        public static string Get(string flag, string fallback) => TryGet(flag, out var v) ? v : fallback;
    }
}
