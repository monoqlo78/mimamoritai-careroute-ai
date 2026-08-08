namespace MimamoriTai.Core.Application;

/// <summary>
/// The household lives in Japan, so life-rhythm analysis (morning / night activity)
/// is always expressed in JST even though everything is stored as UTC.
/// </summary>
public static class HouseholdTime
{
    public static readonly TimeZoneInfo Zone = ResolveZone();

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Tokyo Standard Time", "Asia/Tokyo" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    }

    public static DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    public static DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(ToLocal(utc).DateTime);

    public static TimeOnly LocalTime(DateTimeOffset utc) => TimeOnly.FromDateTime(ToLocal(utc).DateTime);

    /// <summary>UTC instant of local midnight for the given local date.</summary>
    public static DateTimeOffset StartOfLocalDayUtc(DateOnly localDate)
    {
        var localMidnight = localDate.ToDateTime(TimeOnly.MinValue);
        var offset = Zone.GetUtcOffset(localMidnight);
        return new DateTimeOffset(localMidnight, offset).ToUniversalTime();
    }
}
