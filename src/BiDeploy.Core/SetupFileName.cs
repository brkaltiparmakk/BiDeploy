using System;
using System.IO;
using System.Text.RegularExpressions;

namespace BiDeploy.Core
{
    /// <summary>
    /// Mikro setup dosya adından ürün ve mimariyi çıkarır, örn. "Fly_v17xx_Client_Setupx064.exe" → Fly / x64.
    /// Yayıncı aracı bunu varsayılan olarak kullanır, gerekirse parametreyle ezilebilir.
    /// </summary>
    public static class SetupFileName
    {
        public static bool TryInfer(string path, out string product, out string architecture)
        {
            // Yol Windows'tan gelse de (C:\...) her platformda doğru ayrıştırılsın.
            var name = Path.GetFileNameWithoutExtension(path.Substring(path.LastIndexOfAny(new[] { '\\', '/' }) + 1));
            product = "";
            architecture = "";

            var productMatch = Regex.Match(name, @"^([A-Za-z]+)_");
            if (productMatch.Success) product = productMatch.Groups[1].Value;

            if (Regex.IsMatch(name, @"x0?64", RegexOptions.IgnoreCase)) architecture = "x64";
            else if (Regex.IsMatch(name, @"x0?(86|32)", RegexOptions.IgnoreCase)) architecture = "x86";

            return product.Length > 0 && architecture.Length > 0;
        }
    }
}
