using System.Linq;
using SLiveTranslator.Models;
using SLiveTranslator.Translation.Providers;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Pages;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The model combo state machine of a provider card, with a fake provider and a clock the test moves on.</summary>
public class ProviderCardTests
{
    static readonly ModelOption Opus = new("claude-opus-5-5", "Claude Opus 5.5", ModelTags.Recommended, "$4 / $20 per 1M tokens");
    static readonly ModelOption Sonnet = new("claude-sonnet-5-5", "Claude Sonnet 5.5", ModelTags.Balanced);
    static readonly ModelOption Haiku = new("claude-haiku-5-5", "Claude Haiku 5.5", ModelTags.Fastest);
    static readonly ModelOption Old = new("claude-old-4", "Claude Old 4", ModelTags.Deprecated);

    sealed class Card
    {
        public FakeProvider Provider { get; }
        public FakeScheduler Clock { get; } = new();
        public ProviderSettings Section { get; }
        public int Edits { get; private set; }
        public ProviderCardViewModel Vm { get; }

        public Card(ProviderSettings? section = null, FakeProvider? provider = null)
        {
            Provider = provider ?? new FakeProvider(ProviderIds.Claude);
            Section = section ?? new ClaudeProviderSettings();
            Vm = new ProviderCardViewModel(Provider, () => Section, () => Edits++, Clock.Delay);
        }
    }

    static void AssertComboEmptyAndDisabled(ProviderCardViewModel card)
    {
        Assert.Empty(card.Models);
        Assert.False(card.IsModelEnabled);
        Assert.Null(card.SelectedModel);
    }

    [Fact]
    public void Without_a_key_the_combo_is_empty_and_disabled_with_a_hint_and_nothing_is_checked() => SettingsUi.Run(() =>
    {
        var card = new Card();

        Assert.Equal(KeyCheckState.NoKey, card.Vm.State);
        AssertComboEmptyAndDisabled(card.Vm);
        Assert.False(card.Vm.CanCheck);
        Assert.False(card.Vm.CheckCommand.CanExecute(null));
        Assert.Equal("Enter the API key and press Check.", card.Vm.ModelHint);
        Assert.False(card.Vm.InfoOpen);

        _ = card.Vm.CheckAsync();
        card.Vm.Activate();
        card.Clock.Advance(5000);
        Assert.Empty(card.Provider.Checks);
    });

    [Fact]
    public void Typing_checks_800_ms_after_the_last_key_press_and_cancels_the_check_before() => SettingsUi.Run(() =>
    {
        var card = new Card();

        card.Vm.KeyText = "sk-1";
        Assert.Equal(KeyCheckState.Typed, card.Vm.State);
        Assert.True(card.Vm.CanCheck);
        AssertComboEmptyAndDisabled(card.Vm);
        card.Clock.Advance(500);
        card.Vm.KeyText = "sk-12"; // still typing: the wait starts again
        card.Clock.Advance(500);
        Assert.Empty(card.Provider.Checks);
        Assert.Equal(1, card.Clock.Waiting);

        card.Clock.Advance(300);
        var first = Assert.Single(card.Provider.Checks);
        Assert.Equal("sk-12", first.Credentials.ApiKey);
        Assert.Equal(KeyCheckState.Checking, card.Vm.State);
        Assert.True(card.Vm.IsKeyReadOnly);
        Assert.False(card.Vm.CanCheck);
        Assert.Equal("Checking key…", card.Vm.InfoTitle);
        AssertComboEmptyAndDisabled(card.Vm);

        card.Vm.KeyText = "sk-123"; // the key changed while it was checked
        Assert.True(first.Token.IsCancellationRequested);
        first.Reply(SettingsUi.Ok(Opus)); // an answer for the old key is too late
        Assert.Equal(KeyCheckState.Typed, card.Vm.State);
        AssertComboEmptyAndDisabled(card.Vm);

        card.Clock.Advance(800);
        Assert.Equal("sk-123", card.Provider.Checks[1].Credentials.ApiKey);
        card.Provider.Checks[1].Reply(SettingsUi.Ok(Opus));
        Assert.Equal(KeyCheckState.Valid, card.Vm.State);
    });

    [Fact]
    public void Check_starts_at_once_and_replaces_the_waiting_one() => SettingsUi.Run(() =>
    {
        var card = new Card();
        card.Vm.KeyText = "sk-1";

        card.Vm.CheckCommand.Execute(null);
        card.Clock.Advance(800); // the automatic check that was waiting does not come on top

        Assert.Single(card.Provider.Checks);
        Assert.Equal(KeyCheckState.Checking, card.Vm.State);
    });

    [Fact]
    public void A_valid_key_fills_the_combo_with_tags_and_preselects_the_recommended_model_when_none_was_saved() => SettingsUi.Run(() =>
    {
        var card = new Card();
        card.Vm.KeyText = "sk-1";
        Assert.Equal(1, card.Edits); // the typed key is an edit to save
        _ = card.Vm.CheckAsync();

        card.Provider.Checks[0].Reply(new ProviderCheck(CheckStatus.Ok, [Opus, Sonnet, Haiku, Old], new ProviderUsage(1234, 500_000, "characters"), ""));

        Assert.Equal(KeyCheckState.Valid, card.Vm.State);
        Assert.True(card.Vm.IsModelEnabled);
        Assert.Equal(
            ["★ Claude Opus 5.5  ·  Recommended", "Claude Sonnet 5.5  ·  Balanced", "⚡ Claude Haiku 5.5  ·  Fastest", "Claude Old 4 (deprecated)"],
            card.Vm.Models.Select(m => m.Text));
        Assert.Equal("claude-opus-5-5", card.Vm.SelectedModel!.Id);
        Assert.Equal("claude-opus-5-5", card.Section.Model);
        Assert.Equal(2, card.Edits); // a pick to save, not a silent one
        Assert.Equal("Key valid · 4 models", card.Vm.InfoTitle);
        Assert.Equal(MessageKind.Success, card.Vm.InfoKind);
        Assert.Equal("1,234 of 500,000 characters used", card.Vm.UsageText);
        Assert.Equal("Price: $4 / $20 per 1M tokens", card.Vm.CostHint);
        Assert.Equal("", card.Vm.SavedModelWarning);

        card.Vm.SelectedModel = card.Vm.Models[2];
        Assert.Equal("claude-haiku-5-5", card.Section.Model);
        card.Vm.SelectedModel = null!; // the combo emptying itself stores nothing
        Assert.Equal("claude-haiku-5-5", card.Section.Model);
    });

    [Fact]
    public void A_saved_model_on_the_list_stays_selected() => SettingsUi.Run(() =>
    {
        var card = new Card(new ClaudeProviderSettings { Model = "claude-haiku-5-5" });
        card.Vm.KeyText = "sk-1";
        _ = card.Vm.CheckAsync();
        int edits = card.Edits;

        card.Provider.Checks[0].Reply(SettingsUi.Ok(Opus, Sonnet, Haiku));

        Assert.Equal("claude-haiku-5-5", card.Vm.SelectedModel!.Id);
        Assert.Equal("claude-haiku-5-5", card.Section.Model);
        Assert.Equal(edits, card.Edits);
    });

    [Fact]
    public void A_saved_model_the_key_does_not_list_is_kept_with_a_warning_and_never_switched() => SettingsUi.Run(() =>
    {
        var card = new Card(new ClaudeProviderSettings { Model = "claude-retired-3" });
        card.Vm.KeyText = "sk-1";
        _ = card.Vm.CheckAsync();
        int edits = card.Edits;

        card.Provider.Checks[0].Reply(SettingsUi.Ok(Opus, Haiku));

        Assert.Equal(KeyCheckState.Valid, card.Vm.State);
        var kept = card.Vm.SelectedModel!;
        Assert.Equal("claude-retired-3", kept.Id);
        Assert.False(kept.Available);
        Assert.Equal("claude-retired-3 (not available for this key)", kept.Text);
        Assert.Same(kept, card.Vm.Models[0]);
        Assert.Equal("claude-retired-3", card.Section.Model);
        Assert.Equal(edits, card.Edits);
        Assert.Contains("claude-retired-3", card.Vm.SavedModelWarning);
        Assert.Equal(MessageKind.Warning, card.Vm.InfoKind);

        card.Vm.SelectedModel = card.Vm.Models.Single(m => m.Id == "claude-opus-5-5"); // only the user switches
        Assert.Equal("claude-opus-5-5", card.Section.Model);
        Assert.Equal("", card.Vm.SavedModelWarning);
    });

    [Theory]
    [InlineData(CheckStatus.InvalidKey, MessageKind.Error, "The API key is not valid.")]
    [InlineData(CheckStatus.NoPermission, MessageKind.Error, "This key is not allowed to use the service (region or permission).")]
    [InlineData(CheckStatus.NoCredit, MessageKind.Error, "The account has no credit left.")]
    [InlineData(CheckStatus.RateLimited, MessageKind.Warning, "Too many requests right now. Try again in a minute.")]
    [InlineData(CheckStatus.Network, MessageKind.Warning, "The service cannot be reached. Check the connection or the server address.")]
    [InlineData(CheckStatus.Timeout, MessageKind.Warning, "The service did not answer in time.")]
    [InlineData(CheckStatus.Failed, MessageKind.Error, "The check failed.")]
    public void A_failed_check_keeps_the_combo_empty_and_disabled_and_says_why_with_the_raw_detail(CheckStatus status, MessageKind kind, string summary) =>
        SettingsUi.Run(() =>
        {
            var card = new Card(new ClaudeProviderSettings { Model = "claude-opus-5-5" });
            card.Vm.KeyText = "sk-1";
            _ = card.Vm.CheckAsync();

            // A key without credit still lists its models: the combo stays empty all the same.
            card.Provider.Checks[0].Reply(new ProviderCheck(status, [Opus, Haiku], null, "Claude: HTTP 4xx: raw message"));

            Assert.Equal(KeyCheckState.Failed, card.Vm.State);
            AssertComboEmptyAndDisabled(card.Vm);
            Assert.True(card.Vm.InfoOpen);
            Assert.Equal(summary, card.Vm.InfoTitle);
            Assert.Equal("Claude: HTTP 4xx: raw message", card.Vm.InfoMessage);
            Assert.Equal(kind, card.Vm.InfoKind);
            Assert.Equal("claude-opus-5-5", card.Section.Model);
            Assert.True(card.Vm.CanCheck); // the key box and Check are free again
            Assert.False(card.Vm.IsKeyReadOnly);
        });

    [Fact]
    public void Offline_the_card_says_the_saved_model_is_still_used() => SettingsUi.Run(() =>
    {
        var card = new Card(new ClaudeProviderSettings { Model = "claude-opus-5-5" });
        card.Vm.KeyText = "sk-1";
        _ = card.Vm.CheckAsync();
        card.Provider.Checks[0].Reply(ProviderCheck.Fail(CheckStatus.Network, "Claude cannot be reached (No such host is known.)."));

        Assert.Equal("The saved model claude-opus-5-5 will still be used.", card.Vm.OfflineNote);

        _ = card.Vm.CheckAsync();
        card.Provider.Checks[1].Reply(ProviderCheck.Fail(CheckStatus.InvalidKey, ""));
        Assert.Equal("", card.Vm.OfflineNote); // a wrong key is no network problem
    });

    [Fact]
    public void A_saved_key_never_shows_and_is_checked_in_the_background_when_the_card_is_shown() => SettingsUi.Run(() =>
    {
        var section = new ClaudeProviderSettings { ApiKey = "sk-ant-saved", Model = "claude-opus-5-5" };
        var card = new Card(section);

        Assert.Equal("", card.Vm.KeyText);
        Assert.True(card.Vm.HasSavedKey);
        Assert.Equal("Saved. Type a new key to replace it.", card.Vm.KeyPlaceholder);
        Assert.Equal(KeyCheckState.Typed, card.Vm.State);
        AssertComboEmptyAndDisabled(card.Vm);

        card.Vm.Activate();
        card.Vm.Activate(); // shown again while it is checked
        var check = Assert.Single(card.Provider.Checks);
        Assert.Equal("sk-ant-saved", check.Credentials.ApiKey);
        check.Reply(SettingsUi.Ok(Opus, Haiku));
        Assert.Equal("claude-opus-5-5", card.Vm.SelectedModel!.Id);
        card.Vm.Activate();
        Assert.Single(card.Provider.Checks); // checked already

        var target = new ClaudeProviderSettings();
        card.Vm.ApplyKey(target);
        Assert.Equal("", target.ApiKeyProtected); // nothing typed: nothing to store

        card.Vm.KeyText = "sk-ant-new";
        Assert.False(card.Vm.HasSavedKey);
        Assert.True(card.Vm.HasPendingKey);
        card.Vm.ApplyKey(target);
        Assert.Equal("sk-ant-new", target.ApiKey);
        Assert.Equal("sk-ant-saved", section.ApiKey); // the settings being edited keep the saved key until Save
    });

    [Fact]
    public void Emptying_the_box_gives_the_saved_key_back_and_only_remove_key_deletes_it() => SettingsUi.Run(() =>
    {
        var card = new Card(new ClaudeProviderSettings { ApiKey = "sk-ant-saved" });
        var target = new ClaudeProviderSettings { ApiKey = "sk-ant-saved" };

        card.Vm.KeyText = "x"; // a stray key press in the box
        card.Vm.KeyText = "";

        Assert.True(card.Vm.HasSavedKey);
        Assert.False(card.Vm.HasPendingKey);
        Assert.Equal("Saved. Type a new key to replace it.", card.Vm.KeyPlaceholder);
        Assert.Equal(KeyCheckState.Typed, card.Vm.State);
        card.Clock.Advance(800);
        var check = Assert.Single(card.Provider.Checks);
        Assert.Equal("sk-ant-saved", check.Credentials.ApiKey);
        check.Reply(SettingsUi.Ok(Opus));
        card.Vm.ApplyKey(target);
        Assert.Equal("sk-ant-saved", target.ApiKey);

        Assert.True(card.Vm.CanRemoveKey);
        card.Vm.RemoveKeyCommand.Execute(null);

        Assert.Equal(KeyCheckState.NoKey, card.Vm.State);
        AssertComboEmptyAndDisabled(card.Vm);
        Assert.True(card.Vm.HasPendingKey);
        Assert.False(card.Vm.CanRemoveKey);
        Assert.Equal("The saved key is removed when you save. Type a key to keep one.", card.Vm.KeyPlaceholder);
        card.Vm.ApplyKey(target);
        Assert.Equal("", target.ApiKeyProtected);

        card.Vm.KeyText = "sk-ant-new"; // typing after all replaces the key instead
        Assert.Equal("", card.Vm.KeyPlaceholder);
        card.Vm.ApplyKey(target);
        Assert.Equal("sk-ant-new", target.ApiKey);
    });

    [Fact]
    public void A_typed_key_whose_check_was_waiting_is_still_checked_after_the_app_changed_settings() => SettingsUi.Run(() =>
    {
        var card = new Card();
        card.Vm.KeyText = "sk-1";
        card.Clock.Advance(500);

        card.Vm.Reset(keepTyped: true); // the app saved a region meanwhile

        Assert.Equal(KeyCheckState.Typed, card.Vm.State);
        Assert.Equal("sk-1", card.Vm.KeyText);
        card.Clock.Advance(800);
        Assert.Equal("sk-1", Assert.Single(card.Provider.Checks).Credentials.ApiKey);
    });

    [Fact]
    public void A_key_this_account_cannot_decrypt_is_asked_for_again_and_an_emptied_box_removes_it() => SettingsUi.Run(() =>
    {
        const string foreignBlob = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAdamaged"; // from another Windows account
        var card = new Card(new ClaudeProviderSettings { ApiKeyProtected = foreignBlob });

        Assert.Equal(KeyCheckState.NoKey, card.Vm.State);
        Assert.True(card.Vm.SavedKeyUnreadable);
        Assert.Equal("The saved key cannot be read on this Windows account. Enter it again.", card.Vm.KeyPlaceholder);
        Assert.False(card.Vm.HasPendingKey); // an unrelated save keeps the blob
        Assert.True(card.Vm.CanRemoveKey);

        card.Vm.KeyText = "x";
        card.Vm.KeyText = "";

        Assert.True(card.Vm.HasPendingKey);
        var target = new ClaudeProviderSettings { ApiKeyProtected = foreignBlob };
        card.Vm.ApplyKey(target);
        Assert.Equal("", target.ApiKeyProtected);
    });

    [Fact]
    public void A_compatible_server_needs_its_address_and_no_key() => SettingsUi.Run(() =>
    {
        var provider = new FakeProvider(ProviderIds.Compat, ProviderKind.LLM,
            new ProviderFieldSpec(ProviderField.BaseUrl, Required: true), new ProviderFieldSpec(ProviderField.ApiKey, Required: false));
        var section = new CompatProviderSettings();
        var card = new Card(section, provider);

        Assert.Equal(KeyCheckState.NoKey, card.Vm.State);
        Assert.Equal("Enter the server address and press Check.", card.Vm.ModelHint);

        card.Vm.SelectedPreset = card.Vm.PresetChoices.Single(p => p.Value == "ollama");
        Assert.Equal("http://localhost:11434/v1", section.BaseUrl);
        Assert.Equal("ollama", section.Preset);
        Assert.Equal(KeyCheckState.Typed, card.Vm.State);
        card.Clock.Advance(800);

        var check = Assert.Single(provider.Checks);
        Assert.Equal(("", "http://localhost:11434/v1"), (check.Credentials.ApiKey, check.Credentials.BaseUrl));
        Assert.Equal(1, card.Edits);
    });

    [Fact]
    public void An_engine_list_reads_as_engines() => SettingsUi.Run(() =>
    {
        var provider = new FakeProvider(ProviderIds.DeepL, ProviderKind.MT);
        var card = new Card(new ProviderSettings(), provider);
        card.Vm.KeyText = "deepl-key:fx";
        _ = card.Vm.CheckAsync();

        provider.Checks[0].Reply(new ProviderCheck(CheckStatus.Ok,
            [new ModelOption("latency_optimized", "Latency optimized", ModelTags.Recommended | ModelTags.Fastest), new ModelOption("quality_optimized", "Quality optimized")],
            new ProviderUsage(10, null, "characters"), ""));

        Assert.Equal("Engine", card.Vm.ModelLabel);
        Assert.Equal("Key valid · 2 engines", card.Vm.InfoTitle);
        Assert.Equal("★ Latency optimized  ·  Recommended, Fastest", card.Vm.Models[0].Text);
        Assert.Equal("10 characters used (no limit)", card.Vm.UsageText);
        Assert.Equal("latency_optimized", card.Section.Model);
    });

    [Fact]
    public void After_a_save_the_check_of_the_saved_key_stays_and_another_key_drops_it() => SettingsUi.Run(() =>
    {
        var section = new ClaudeProviderSettings();
        var card = new Card(section);
        card.Vm.KeyText = "sk-ant-1";
        _ = card.Vm.CheckAsync();
        card.Provider.Checks[0].Reply(SettingsUi.Ok(Opus, Haiku));

        card.Vm.ApplyKey(section); // what a save stores
        card.Vm.Reset();

        Assert.Equal("", card.Vm.KeyText);
        Assert.True(card.Vm.HasSavedKey);
        Assert.Equal(KeyCheckState.Valid, card.Vm.State);
        Assert.Equal("claude-opus-5-5", card.Vm.SelectedModel!.Id);

        card.Vm.KeyText = "sk-ant-2";
        card.Vm.Reset(); // discarded: back to the saved key, whose check is gone with the typing
        Assert.Equal(KeyCheckState.Typed, card.Vm.State);
        AssertComboEmptyAndDisabled(card.Vm);
    });
}
