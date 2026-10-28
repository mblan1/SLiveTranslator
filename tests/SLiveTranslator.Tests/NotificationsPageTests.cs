using System;
using System.Linq;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Services;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The Notifications page edits <see cref="NotificationSettings"/> of the settings being edited, and its test uses them unsaved.</summary>
public class NotificationsPageTests
{
    static (FakeHost Host, SettingsViewModel Vm) Make(TempDir dir)
    {
        var host = new FakeHost(dir.File("Profiles"));
        return (host, new SettingsViewModel(host, new FakeScheduler().Delay) { Prompts = new FakePrompts() });
    }

    [Fact]
    public void Each_kind_of_notice_shows_its_settings_and_edits_them()
    {
        using var dir = new TempDir();
        var (host, vm) = Make(dir);
        var page = vm.Notifications;

        Assert.Equal([NoticeCategory.Status, NoticeCategory.Warning, NoticeCategory.Error, NoticeCategory.Quota], page.Categories.Select(c => c.Category));
        Assert.Equal(["Status", "Warnings", "Errors", "Quota"], page.Categories.Select(c => c.Name));
        Assert.Equal([2.5, 8, 6, 8], page.Categories.Select(c => c.Seconds));
        Assert.All(page.Categories, c => Assert.True(c.Enabled));

        page.Categories[1].Seconds = 4.26;
        page.Categories[3].Enabled = false;
        page.Categories[2].Seconds = 90; // above the longest a notice stays

        var n = vm.Working.Notifications;
        Assert.Equal((4.3, false, 30.0), (n.Warning.Seconds, n.Quota.Enabled, n.Error.Seconds));
        Assert.Equal(SettingsChange.Notifications, vm.PendingChanges);
        Assert.Equal(8, host.Current.Notifications.Warning.Seconds); // in use only after Save
    }

    [Fact]
    public void Position_sound_balloons_badge_and_mute_are_bound_to_the_settings()
    {
        using var dir = new TempDir();
        var (_, vm) = Make(dir);
        var page = vm.Notifications;
        Assert.Equal(["Top centre", "Top right", "Next to the subtitle region"], page.Positions.Select(p => p.Label));
        Assert.Equal(NoticePosition.TopCenter, page.SelectedPosition.Value);

        page.SelectedPosition = page.Positions.Single(p => p.Value == NoticePosition.NearRegion);
        page.Sound = true;
        page.TrayBalloons = false;
        page.LiveBadge = false;
        page.Muted = true;

        var n = vm.Working.Notifications;
        Assert.Equal((NoticePosition.NearRegion, true, false, false, true), (n.Position, n.Sound, n.TrayBalloons, n.LiveBadge, n.Muted));
        Assert.True(vm.HasChanges);

        vm.Discard();
        Assert.Equal((NoticePosition.TopCenter, false, true), (page.SelectedPosition.Value, page.Sound, page.TrayBalloons));
        Assert.Equal(2.5, page.Categories[0].Seconds);
    }

    [Fact]
    public async Task Send_test_shows_the_page_as_it_is_without_saving_it()
    {
        using var dir = new TempDir();
        var (host, vm) = Make(dir);
        vm.Notifications.Categories[0].Enabled = false;
        vm.Notifications.Sound = true;

        await vm.Notifications.TestCommand.ExecuteAsync(null);

        var tested = Assert.Single(host.TestedNotices);
        Assert.False(tested.Status.Enabled);
        Assert.True(tested.Sound);
        Assert.NotSame(vm.Working.Notifications, tested);
        Assert.Empty(host.Saves);
    }

    [Fact]
    public async Task The_notifier_test_takes_settings_that_are_not_saved_yet()
    {
        var surface = new FakeNoticeSurface();
        var inUse = new NotificationSettings();
        var notifier = new Notifier(surface, () => inUse, delay: _ => Task.CompletedTask);
        var page = new NotificationSettings { Position = NoticePosition.TopRight, Warning = { Enabled = false }, Error = { Enabled = false }, Quota = { Enabled = false } };

        await notifier.SendTestAsync(page);

        var toast = Assert.Single(surface.Toasts);
        Assert.Equal((Loc.Instance["Notice.Test.Status"], NoticePosition.TopRight), (toast.Text, toast.Position));
    }

    [Fact]
    public void A_language_switch_writes_the_kinds_and_positions_again_and_keeps_the_selection()
    {
        using var dir = new TempDir();
        var (_, vm) = Make(dir);
        vm.Notifications.SelectedPosition = vm.Notifications.Positions.Single(p => p.Value == NoticePosition.TopRight);
        var before = vm.Notifications.Positions;
        int nameChanges = 0;
        vm.Notifications.Categories[0].PropertyChanged += (_, _) => nameChanges++;

        vm.Notifications.Relocalize();

        Assert.NotSame(before, vm.Notifications.Positions);
        Assert.Equal(NoticePosition.TopRight, vm.Notifications.SelectedPosition.Value);
        Assert.True(nameChanges > 0);
        Assert.False(vm.PendingChanges.HasFlag(SettingsChange.Language));
    }
}
