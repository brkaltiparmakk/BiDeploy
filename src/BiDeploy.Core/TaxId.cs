using System;

namespace BiDeploy.Core
{
    /// <summary>VKN (10 hane) ve şahıs firmaları için TCKN (11 hane) doğrulaması.</summary>
    public static class TaxId
    {
        public static bool IsValid(string? value) => IsValidVkn(value) || IsValidTckn(value);

        public static bool IsValidVkn(string? vkn)
        {
            if (vkn == null || vkn.Length != 10 || !AllDigits(vkn)) return false;
            var sum = 0;
            for (var i = 0; i < 9; i++)
            {
                var digit = vkn[i] - '0';
                var tmp = (digit + 10 - (i + 1)) % 10;
                if (tmp == 0)
                {
                    // 0 ise katkısı 0'dır.
                    continue;
                }
                var pow = ModPow(2, 10 - (i + 1), 9);
                var val = tmp * pow % 9;
                if (val == 0) val = 9;
                sum += val;
            }
            var check = (10 - sum % 10) % 10;
            return check == vkn[9] - '0';
        }

        public static bool IsValidTckn(string? tckn)
        {
            if (tckn == null || tckn.Length != 11 || !AllDigits(tckn) || tckn[0] == '0') return false;
            var d = new int[11];
            for (var i = 0; i < 11; i++) d[i] = tckn[i] - '0';
            var odd = d[0] + d[2] + d[4] + d[6] + d[8];
            var even = d[1] + d[3] + d[5] + d[7];
            var d10 = ((odd * 7 - even) % 10 + 10) % 10;
            if (d10 != d[9]) return false;
            var sum = 0;
            for (var i = 0; i < 10; i++) sum += d[i];
            return sum % 10 == d[10];
        }

        private static bool AllDigits(string s)
        {
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private static int ModPow(int b, int e, int m)
        {
            var result = 1;
            for (var i = 0; i < e; i++) result = result * b % m;
            return result;
        }
    }
}
