using Microsoft.JSInterop;

namespace StreamDeckPilot.Web.Services;

/// <summary>
/// Formats times for the person looking at the page (#99): the server renders the page (Interactive
/// Server), so it asks the browser once for its time zone. Ages are computed when the page renders,
/// which happens on every pushed update; between updates they are not refreshed (no UI timer, #108),
/// so pages state "as of" the time of their last update.
/// </summary>
public sealed class BrowserClock(IJSRuntime js, TimeProvider time)
{
    private TimeZoneInfo? _zone;

    /// <summary>The browser's zone once <see cref="LoadAsync"/> ran; UTC until then (e.g. while prerendering).</summary>
    public TimeZoneInfo Zone => _zone ?? TimeZoneInfo.Utc;

    public bool IsLoaded => _zone is not null;

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>Asks the browser for its zone. Call from <c>OnAfterRenderAsync</c>; cheap after the first call.</summary>
    public async Task<bool> LoadAsync()
    {
        if (_zone is not null) return false;
        try
        {
            var info = await js.InvokeAsync<BrowserZone?>("sdpClock.zone");
            _zone = Resolve(info);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            _zone = TimeZoneInfo.Utc;
        }
        return true;
    }

    /// <summary>A clock time in the browser's zone: "14:03:12", or with the date when it isn't today.</summary>
    public string Time(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, Zone);
        var today = TimeZoneInfo.ConvertTime(Now, Zone).Date;
        var text = local.Date == today ? local.ToString("HH:mm:ss") : local.ToString("yyyy-MM-dd HH:mm:ss");
        return IsLoaded ? text : text + " UTC";
    }

    /// <summary>"just now", "42 s ago", "5 min ago", "3 h ago", "2 d ago": the age when the page rendered.</summary>
    public string Age(DateTimeOffset at) => FormatAge(Now - at);

    public static string FormatAge(TimeSpan age) => age.TotalSeconds switch
    {
        < 2 => "just now",
        < 60 => $"{(int)age.TotalSeconds} s ago",
        < 3600 => $"{(int)age.TotalMinutes} min ago",
        < 86400 => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} d ago",
    };

    private static TimeZoneInfo Resolve(BrowserZone? info)
    {
        if (info is null) return TimeZoneInfo.Utc;
        if (!string.IsNullOrEmpty(info.Id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(info.Id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        // No zone database in the image, or an unknown id: fall back to the browser's current offset.
        var offset = TimeSpan.FromMinutes(-info.OffsetMinutes);
        return TimeZoneInfo.CreateCustomTimeZone($"UTC{offset:+hh\\:mm;-hh\\:mm}", offset, null, null);
    }

    private sealed record BrowserZone(string? Id, int OffsetMinutes);
}
