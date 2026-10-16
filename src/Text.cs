using System.Globalization;

namespace Loupe
{
    /// <summary>Minimal localization: English by default, Spanish when the Windows UI language is Spanish.</summary>
    static class Text
    {
        static readonly bool Spanish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";

        public static string T(string english, string spanish)
        {
            return Spanish ? spanish : english;
        }
    }
}
