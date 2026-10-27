using System;

namespace SLiveTranslator.Tests;

/// <summary>A clock the test sets; its local time zone is fixed so "midnight" does not depend on the PC.</summary>
sealed class TestClock(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone { get; } = zone ?? TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7");

    public void Advance(TimeSpan by) => Now += by;
}
