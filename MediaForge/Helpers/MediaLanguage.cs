using MediaForge.Models;
using System.Globalization;

namespace MediaForge.Helpers;

internal static class MediaLanguage
{
    public static string SystemLanguage
    {
        get
        {
            string language = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault()
                ?? CultureInfo.InstalledUICulture.Name;
            return Normalize(language);
        }
    }

    public static string Normalize(string language)
    {
        string code = language.Trim().Replace('_', '-').ToLowerInvariant();
        code = code.Split('-')[0];
        code = code switch
        {
            "cze" => "ces", "ger" => "deu", "fre" => "fra", "slo" => "slk",
            "dut" => "nld", "rum" => "ron", "gre" => "ell", "chi" => "zho",
            "alb" => "sqi", "arm" => "hye", "baq" => "eus", "bur" => "mya",
            "geo" => "kat", "ice" => "isl", "mac" => "mkd", "may" => "msa",
            "per" => "fas", "tib" => "bod", "wel" => "cym", _ => code
        };
        if (code.Length == 3)
        {
            var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(c => c.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (culture is not null) return culture.TwoLetterISOLanguageName;
        }
        return code;
    }

    public static string Display(string language)
    {
        string code = Normalize(language);
        if (code is "" or "und") return "jazyk neurčen";
        try { return $"{CultureInfo.GetCultureInfo(code).DisplayName} ({language})"; }
        catch (CultureNotFoundException) { return language; }
    }

    public static VideoStreamInfo? SelectAudio(IEnumerable<VideoStreamInfo> streams, string language) =>
        streams.Where(s => s.Type == "audio")
            .OrderByDescending(s => Normalize(s.Language) == Normalize(language))
            .ThenByDescending(s => s.IsDefault).ThenBy(s => s.Index).FirstOrDefault();
}
