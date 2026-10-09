using System.Globalization;
using System.Text;

namespace EspansoEditor.Core.Text;

/// <summary>Formats a date with Espanso's strftime-style codes (%d, %m, %Y, %H ...), for previews.</summary>
public static class StrfTime
{
    public static string Format(string format, DateTime t, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var sb = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%' || i + 1 >= format.Length)
            {
                sb.Append(c);
                continue;
            }
            var code = format[++i];
            // Flags like %-d (no padding) are common; support '-' only.
            var noPad = false;
            if (code == '-' && i + 1 < format.Length)
            {
                noPad = true;
                code = format[++i];
            }
            string Pad(int v, int width) => noPad ? v.ToString(CultureInfo.InvariantCulture) : v.ToString(new string('0', width), CultureInfo.InvariantCulture);
            sb.Append(code switch
            {
                'Y' => t.Year.ToString(CultureInfo.InvariantCulture),
                'y' => Pad(t.Year % 100, 2),
                'm' => Pad(t.Month, 2),
                'd' => Pad(t.Day, 2),
                'e' => t.Day.ToString(CultureInfo.InvariantCulture).PadLeft(noPad ? 1 : 2),
                'H' => Pad(t.Hour, 2),
                'I' => Pad(t.Hour % 12 == 0 ? 12 : t.Hour % 12, 2),
                'M' => Pad(t.Minute, 2),
                'S' => Pad(t.Second, 2),
                'p' => t.Hour < 12 ? "AM" : "PM",
                'P' => t.Hour < 12 ? "am" : "pm",
                'A' => culture.DateTimeFormat.GetDayName(t.DayOfWeek),
                'a' => culture.DateTimeFormat.GetAbbreviatedDayName(t.DayOfWeek),
                'B' => culture.DateTimeFormat.GetMonthName(t.Month),
                'b' or 'h' => culture.DateTimeFormat.GetAbbreviatedMonthName(t.Month),
                'j' => Pad(t.DayOfYear, 3),
                'u' => ((int)t.DayOfWeek == 0 ? 7 : (int)t.DayOfWeek).ToString(CultureInfo.InvariantCulture),
                'w' => ((int)t.DayOfWeek).ToString(CultureInfo.InvariantCulture),
                'D' => t.ToString("MM/dd/yy", CultureInfo.InvariantCulture),
                'F' => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                'T' => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                'R' => t.ToString("HH:mm", CultureInfo.InvariantCulture),
                'x' => t.ToString("d", culture),
                'X' => t.ToString("T", culture),
                'c' => t.ToString("F", culture),
                'n' => "\n",
                't' => "\t",
                '%' => "%",
                _ => "%" + (noPad ? "-" : "") + code,
            });
        }
        return sb.ToString();
    }
}
