using System;

namespace BiDeploy.Core
{
    /// <summary>"17.7.4.46277" biçimindeki sürümleri karşılaştırır.</summary>
    public static class MikroVersion
    {
        public static bool TryParse(string? text, out Version version)
        {
            version = new Version(0, 0);
            if (string.IsNullOrWhiteSpace(text)) return false;
            // FileVersionInfo bazen "17.7.4.46277 " gibi boşluklu ya da virgüllü döner.
            var normalized = text!.Trim().Replace(',', '.').Replace(" ", "");
            if (!Version.TryParse(normalized, out var parsed)) return false;
            version = parsed;
            return true;
        }

        public static Version Parse(string text) =>
            TryParse(text, out var v) ? v : throw new FormatException($"Geçersiz sürüm: '{text}'");

        /// <summary>a &lt; b ise negatif, eşitse 0, a &gt; b ise pozitif. Parse edilemeyen sürüm en küçük sayılır.</summary>
        public static int Compare(string? a, string? b)
        {
            var hasA = TryParse(a, out var va);
            var hasB = TryParse(b, out var vb);
            if (!hasA && !hasB) return 0;
            if (!hasA) return -1;
            if (!hasB) return 1;
            return Normalize(va).CompareTo(Normalize(vb));
        }

        public static bool AreEqual(string? a, string? b) => Compare(a, b) == 0 && TryParse(a, out _);

        // "17.7" ile "17.7.0.0" eşit sayılsın.
        private static Version Normalize(Version v) =>
            new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}
