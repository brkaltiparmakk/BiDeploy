namespace BiDeploy.Server.Services;

/// <summary>Panelde saatler sunucunun saat diliminden (Docker'da UTC) bağımsız olarak Türkiye saatiyle gösterilir.</summary>
public static class TurkeyTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public static DateTime From(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static string Format(DateTime? utc, string format, string ifEmpty = "") =>
        utc == null ? ifEmpty : From(utc.Value).ToString(format);
}
