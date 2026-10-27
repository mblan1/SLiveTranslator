using System;
using System.Collections.Generic;
using System.Linq;
using SLiveTranslator.Hotkeys;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;
using Xunit;
using DRect = System.Drawing.Rectangle;

namespace SLiveTranslator.Tests;

public class AlertRulesTests
{
    const string Claude = "claude/claude-opus-5-5";

    sealed class Harness
    {
        public FakeNoticeSurface Surface { get; } = new();
        public NotificationSettings Settings { get; } = new();
        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero)); // 17:00 at UTC+7
        public AlertRules Rules { get; }

        public Harness() => Rules = new AlertRules(new Notifier(Surface, () => Settings, clock: Clock), Clock);

        public List<(string Text, StatusKind Kind)> Shown => Surface.Toasts.Select(t => (t.Text, t.Kind)).ToList();
    }

    [Fact]
    public void The_selected_provider_giving_way_is_a_quota_notice_with_the_reason_and_its_return_a_status_notice()
    {
        var h = new Harness();
        var region = new DRect(400, 900, 1200, 120);

        h.Rules.ServiceSwitched(new ServiceSwitch(Claude, ProviderKeys.Free, "Claude: HTTP 400 billing_error", CheckStatus.NoCredit), Claude, "Claude", region);
        h.Clock.Advance(AlertRules.ShortOutage); // the user added credit
        h.Rules.ServiceSwitched(new ServiceSwitch(ProviderKeys.Free, Claude, ""), Claude, "Claude", region);

        Assert.Equal(
        [
            ("Claude is not answering (no credit or quota left); the free services translate until it is back.", StatusKind.Warning),
            ("Claude is answering again.", StatusKind.On),
        ], h.Shown);
        Assert.Equal(region, h.Surface.Toasts[0].Anchor);
    }

    [Fact]
    public void A_provider_that_times_out_now_and_then_is_said_once_per_five_minutes_and_its_quick_returns_are_not_said()
    {
        var h = new Harness();
        var timeout = new ServiceSwitch(Claude, ProviderKeys.Free, "Claude: no answer in 15 s", CheckStatus.Timeout);
        var back = new ServiceSwitch(ProviderKeys.Free, Claude, "");

        for (int i = 0; i < 6; i++) // down for the 30 s pause, then answering for 30 s, for six minutes
        {
            h.Rules.ServiceSwitched(timeout, Claude, "Claude", null);
            h.Clock.Advance(FallbackTranslator.DefaultPause);
            h.Rules.ServiceSwitched(back, Claude, "Claude", null);
            h.Clock.Advance(TimeSpan.FromSeconds(30));
        }
        h.Rules.ServiceSwitched(timeout with { Reason = "Claude: HTTP 402", Status = CheckStatus.NoCredit }, Claude, "Claude", null); // another cause

        Assert.Equal(
        [
            "Claude is not answering (no answer in time); the free services translate until it is back.",
            "Claude is not answering (no answer in time); the free services translate until it is back.", // five minutes later
            "Claude is not answering (no credit or quota left); the free services translate until it is back.",
        ], h.Shown.Select(s => s.Text));
    }

    [Fact]
    public void A_return_is_said_only_after_a_said_outage()
    {
        var h = new Harness();
        h.Settings.Quota.Enabled = false;

        h.Rules.ServiceSwitched(new ServiceSwitch(Claude, ProviderKeys.Free, "Claude: HTTP 529", CheckStatus.Failed), Claude, "Claude", null);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Rules.ServiceSwitched(new ServiceSwitch(ProviderKeys.Free, Claude, ""), Claude, "Claude", null);

        Assert.Empty(h.Surface.Toasts);
    }

    [Fact]
    public void A_failure_of_unknown_kind_is_named_as_an_error()
    {
        var h = new Harness();

        h.Rules.ServiceSwitched(new ServiceSwitch(Claude, ProviderKeys.Free, "Claude returned an unexpected response"), Claude, "Claude", null);

        Assert.Contains("(an error)", Assert.Single(h.Shown).Text);
    }

    [Fact]
    public void The_free_services_switching_among_themselves_says_nothing()
    {
        var h = new Harness();

        h.Rules.ServiceSwitched(new ServiceSwitch("google", "mymemory", "Google blocked."), ProviderKeys.Free, "Free", null);
        h.Rules.ServiceSwitched(new ServiceSwitch("mymemory", "google", ""), ProviderKeys.Free, "Free", null);
        h.Rules.ServiceSwitched(new ServiceSwitch("google", "mymemory", "Google blocked."), Claude, "Claude", null); // behind Claude

        Assert.Empty(h.Surface.Toasts);
    }

    [Fact]
    public void MyMemory_warns_at_eighty_percent_and_errors_at_the_limit_once_per_local_day()
    {
        var h = new Harness();

        h.Rules.MyMemoryUsage(3_900, 5_000, hasEmail: false);
        h.Rules.MyMemoryUsage(4_000, 5_000, hasEmail: false);
        h.Rules.MyMemoryUsage(4_500, 5_000, hasEmail: false); // already warned today
        h.Rules.MyMemoryUsage(5_000, 5_000, hasEmail: false);
        h.Rules.MyMemoryUsage(5_100, 5_000, hasEmail: false);

        Assert.Equal(2, h.Shown.Count);
        Assert.Equal(StatusKind.Warning, h.Shown[0].Kind);
        Assert.Contains("4,000 of today's 5,000", h.Shown[0].Text);
        Assert.Contains("Add an email", h.Shown[0].Text);
        Assert.Equal(StatusKind.Error, h.Shown[1].Kind);
        Assert.Contains("5,000 free characters for today are used up", h.Shown[1].Text);

        h.Clock.Advance(TimeSpan.FromHours(8)); // past local midnight: a new count, new notices
        h.Rules.MyMemoryUsage(40_000, 50_000, hasEmail: true);
        Assert.Equal(3, h.Shown.Count);
        Assert.DoesNotContain("Add an email", h.Shown[2].Text);
        Assert.Contains("API key", h.Shown[2].Text);
    }

    [Fact]
    public void Reaching_the_limit_at_once_says_only_that()
    {
        var h = new Harness();

        h.Rules.MyMemoryUsage(50_000, 50_000, hasEmail: true);
        h.Rules.MyMemoryUsage(50_010, 50_000, hasEmail: true);

        Assert.Equal(StatusKind.Error, Assert.Single(h.Shown).Kind);
    }

    [Fact]
    public void Every_failing_hotkey_is_named_with_its_reason_in_one_notice()
    {
        var hotkeys = new HotkeySettings();
        hotkeys[HotkeyAction.History] = "F12";
        hotkeys[HotkeyAction.Clear] = "Ctrl+Shift+T";
        var results = new Dictionary<HotkeyAction, HotkeyResult>
        {
            [HotkeyAction.TranslateScreen] = HotkeyResult.InUse,
            [HotkeyAction.ToggleSubtitles] = HotkeyResult.Ok,
            [HotkeyAction.SelectRegion] = HotkeyResult.Disabled,
            [HotkeyAction.Clear] = HotkeyResult.Duplicate,
            [HotkeyAction.History] = HotkeyResult.Reserved,
        };

        string? problem = HotkeyText.Problems(results, hotkeys);

        Assert.Equal(
            "Some hotkeys do not work. Change them in Settings:\n" +
            "Ctrl+Shift+T (Translate screen): used by another program\n" +
            "Ctrl+Shift+T (Clear): also set for another action\n" +
            "F12 (History): F12 is reserved for debuggers", problem);
        Assert.Null(HotkeyText.Problems(results.ToDictionary(r => r.Key, r => r.Value is HotkeyResult.Disabled ? r.Value : HotkeyResult.Ok), hotkeys));
    }
}
