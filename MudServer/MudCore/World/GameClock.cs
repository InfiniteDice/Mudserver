using System;

namespace MudCore.World
{
    /// <summary>
    /// In-game calendar/clock.
    /// Rules:
    /// - 60 seconds / minute
    /// - 60 minutes / hour
    /// - 24 hours / day
    /// - 30 days / month
    /// - 12 months / year
    /// </summary>
    public class GameClock
    {
        public long Year { get; private set; } = 1;
        public int Month { get; private set; } = 1; // 1..12
        public int Day { get; private set; } = 1;   // 1..30
        public int Hour { get; private set; } = 0;  // 0..23
        public int Minute { get; private set; } = 0;// 0..59
        public int Second { get; private set; } = 0;// 0..59

        private const int SecondsPerMinute = 60;
        private const int MinutesPerHour = 60;
        private const int HoursPerDay = 24;
        private const int DaysPerMonth = 30;
        private const int MonthsPerYear = 12;

        private static readonly string[] MonthNames = new[]
        {
            "January","February","March","April","May","June",
            "July","August","September","October","November","December"
        };

        public GameClock() { }

        public GameClock(long year, int month, int day, int hour, int minute, int second)
        {
            Year = Math.Max(1, year);
            Month = Clamp(month, 1, MonthsPerYear);
            Day = Clamp(day, 1, DaysPerMonth);
            Hour = Clamp(hour, 0, HoursPerDay - 1);
            Minute = Clamp(minute, 0, MinutesPerHour - 1);
            Second = Clamp(second, 0, SecondsPerMinute - 1);
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        public void AdvanceSeconds(long seconds)
        {
            if (seconds <= 0) return;

            long totalSeconds = Second + seconds;
            Second = (int)(totalSeconds % SecondsPerMinute);
            long carryMinutes = totalSeconds / SecondsPerMinute;

            long totalMinutes = Minute + carryMinutes;
            Minute = (int)(totalMinutes % MinutesPerHour);
            long carryHours = totalMinutes / MinutesPerHour;

            long totalHours = Hour + carryHours;
            Hour = (int)(totalHours % HoursPerDay);
            long carryDays = totalHours / HoursPerDay;

            long totalDays = (Day - 1) + carryDays; // zero-based
            Day = (int)((totalDays % DaysPerMonth) + 1);
            long carryMonths = totalDays / DaysPerMonth;

            long totalMonths = (Month - 1) + carryMonths;
            Month = (int)((totalMonths % MonthsPerYear) + 1);
            long carryYears = totalMonths / MonthsPerYear;

            Year += carryYears;
        }

        public string ToIsoString()
        {
            return $"{Year:D4}-{Month:D2}-{Day:D2}T{Hour:D2}:{Minute:D2}:{Second:D2}";
        }

        public static GameClock Parse(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return new GameClock();

            iso = iso.Trim();
            var sepIndex = iso.IndexOf('T');
            if (sepIndex < 0) sepIndex = iso.IndexOf(' ');
            try
            {
                string datePart = sepIndex > 0 ? iso.Substring(0, sepIndex) : iso;
                string timePart = sepIndex > 0 ? iso.Substring(sepIndex + 1) : "00:00:00";

                var dateTokens = datePart.Split('-', StringSplitOptions.RemoveEmptyEntries);
                var timeTokens = timePart.Split(':', StringSplitOptions.RemoveEmptyEntries);

                int y = dateTokens.Length > 0 && int.TryParse(dateTokens[0], out var yy) ? yy : 1;
                int m = dateTokens.Length > 1 && int.TryParse(dateTokens[1], out var mm) ? mm : 1;
                int d = dateTokens.Length > 2 && int.TryParse(dateTokens[2], out var dd) ? dd : 1;

                int hh = timeTokens.Length > 0 && int.TryParse(timeTokens[0], out var hhh) ? hhh : 0;
                int min = timeTokens.Length > 1 && int.TryParse(timeTokens[1], out var mmm) ? mmm : 0;
                int sec = timeTokens.Length > 2 && int.TryParse(timeTokens[2], out var sss) ? sss : 0;

                return new GameClock(Math.Max(1, y), Clamp(m, 1, MonthsPerYear), Clamp(d, 1, DaysPerMonth),
                                     Clamp(hh, 0, HoursPerDay - 1), Clamp(min, 0, MinutesPerHour - 1), Clamp(sec, 0, SecondsPerMinute - 1));
            }
            catch
            {
                return new GameClock();
            }
        }

        /// <summary>
        /// Short human descriptor mapping hour -> period label.
        /// Transitions (1-hour): Midnight(0), Dawn(6), Noon(12), Dusk(18)
        /// Long periods (5-hours): Night(1-5), Morning(7-11), Afternoon(13-17), Evening(19-23)
        /// </summary>
        public string GetPeriodDescriptor()
        {
            // Transitions first
            if (Hour == 0) return "Midnight";
            if (Hour == 6) return "Dawn";
            if (Hour == 12) return "Noon";
            if (Hour == 18) return "Dusk";

            // Long periods
            if (Hour >= 1 && Hour <= 5) return "Night";
            if (Hour >= 7 && Hour <= 11) return "Morning";
            if (Hour >= 13 && Hour <= 17) return "Afternoon";
            // Hour 19..23
            return "Evening";
        }

        /// Helper: returns true when the clock's current period descriptor equals the provided periodName.
        /// Matching is case-insensitive and expects the standard period labels used by GetPeriodDescriptor:
        /// "Midnight","Dawn","Noon","Dusk","Night","Morning","Afternoon","Evening".
        /// </summary>
        public bool IsInPeriod(string periodName)
        {
            if (string.IsNullOrWhiteSpace(periodName)) return false;
            try
            {
                var cur = GetPeriodDescriptor();
                return string.Equals(cur, periodName.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns display date like "5th day of June, Year 345"
        /// </summary>
        public string GetDisplayDate()
        {
            var monthName = (Month >= 1 && Month <= 12) ? MonthNames[Month - 1] : $"Month{Month}";
            return $"{GetOrdinal(Day)} day of {monthName}, Year {Year}";
        }

        /// <summary>
        /// Returns time formatted as "HH:mm:ss"
        /// </summary>
        public string GetDisplayTime()
        {
            return $"{Hour:D2}:{Minute:D2}:{Second:D2}";
        }

        /// <summary>
        /// Combined header: "Period, 5th day of June, Year 345"
        /// </summary>
        public string GetHeaderString()
        {
            return $"{GetPeriodDescriptor()}, {GetDisplayDate()}";
        }

        private static string GetOrdinal(int n)
        {
            if (n <= 0) return n.ToString();
            int rem100 = n % 100;
            if (rem100 >= 11 && rem100 <= 13) return n + "th";
            switch (n % 10)
            {
                case 1: return n + "st";
                case 2: return n + "nd";
                case 3: return n + "rd";
                default: return n + "th";
            }
        }

        private static readonly Dictionary<string, (int StartHour, int EndHour)> PeriodHourRanges =
            new(StringComparer.OrdinalIgnoreCase)
        {
            { "Midnight", (0, 0) },
            { "Dawn", (6, 6) },
            { "Noon", (12, 12) },
            { "Dusk", (18, 18) },
            { "Night", (1, 5) },
            { "Morning", (7, 11) },
            { "Afternoon", (13, 17) },
            { "Evening", (19, 23) }
        };

        public static string FormatPeriodHourRange(string periodName)
        {
            if (string.IsNullOrWhiteSpace(periodName)) return string.Empty;
            if (!PeriodHourRanges.TryGetValue(periodName.Trim(), out var range)) return string.Empty;

            string span = range.StartHour == range.EndHour
                ? range.StartHour.ToString()
                : $"{range.StartHour}-{range.EndHour}";

            return $"{periodName} spans: {span}";
        }

        public static bool TryGetPeriodHourRange(string periodName, out (int StartHour, int EndHour) range)
        {
            if (string.IsNullOrWhiteSpace(periodName))
            {
                range = default;
                return false;
            }

            return PeriodHourRanges.TryGetValue(periodName.Trim(), out range);
        }

        public override string ToString() => ToIsoString();

        /// <summary>
        /// Calculates the season code based on the current month (1-12, where 1=January, 12=December).
        /// Returns: 'W' (Winter), 'Sp' (Spring), 'Su' (Summer), 'F' (Fall)
        /// Mapping:
        /// - Winter: Dec(12), Jan(1), Feb(2) -> 'W'
        /// - Spring: Mar(3), Apr(4), May(5) -> 'Sp'
        /// - Summer: Jun(6), Jul(7), Aug(8) -> 'Su'
        /// - Fall: Sep(9), Oct(10), Nov(11) -> 'F'
        /// </summary>
        public string CalculateSeason()
        {
            return CalculateSeason(this.Month);
        }

        /// <summary>
        /// Static overload: Calculates season based on a given month (1-12).
        /// Returns: 'W' (Winter), 'Sp' (Spring), 'Su' (Summer), 'F' (Fall)
        /// </summary>
        public static string CalculateSeason(int month)
        {
            // Clamp month to valid range (1-12)
            month = Clamp(month, 1, MonthsPerYear);

            // Winter: December(12), January(1), February(2)
            if (month == 12 || month == 1 || month == 2)
                return "W";

            // Spring: March(3), April(4), May(5)
            if (month >= 3 && month <= 5)
                return "Sp";

            // Summer: June(6), July(7), August(8)
            if (month >= 6 && month <= 8)
                return "Su";

            // Fall: September(9), October(10), November(11)
            return "F";
        }
    }
}