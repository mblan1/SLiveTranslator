using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using SLiveTranslator.Models;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

public class SettingsWindowTests
{
    /// <summary>A key blob from another Windows account: it shows as an empty box.</summary>
    const string ForeignBlob = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAdamaged";

    static void OnStaThread(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void Undecryptable_key_survives_an_unrelated_save_and_an_emptied_box_removes_it() => OnStaThread(() =>
    {
        var settings = new AppSettings();
        settings.Translation.Provider = ProviderIds.Claude;
        settings.Translation.Claude.ApiKeyProtected = ForeignBlob;
        int saves = 0;
        var window = new SettingsWindow(settings, () => null, () => saves++);

        Assert.Equal("", window.ClaudeKey.Password);
        Assert.True(window.TryApply());
        Assert.Equal(ForeignBlob, settings.Translation.Claude.ApiKeyProtected);

        window.ClaudeKey.Password = "x";
        window.ClaudeKey.Password = "";
        Assert.True(window.TryApply());
        Assert.Equal("", settings.Translation.Claude.ApiKeyProtected);
        Assert.False(settings.Translation.Claude.ApiKeyUnreadable);
        Assert.Equal(2, saves);
        window.Close();
    });

    [Fact]
    public void Without_a_saved_model_claude_gets_the_recommended_one_and_a_saved_model_stays() => OnStaThread(() =>
    {
        // This window has no model list: an empty box would leave Claude unusable ("no model is picked").
        var fresh = new AppSettings();
        fresh.Translation.Provider = ProviderIds.Claude;
        var window = new SettingsWindow(fresh, () => null, () => { });
        Assert.True(window.TryApply());
        Assert.Equal("claude-opus-5-5", fresh.Translation.Claude.Model);
        window.Close();

        var saved = new AppSettings();
        saved.Translation.Claude.Model = "claude-haiku-5-5";
        window = new SettingsWindow(saved, () => null, () => { });
        Assert.True(window.TryApply());
        Assert.Equal("claude-haiku-5-5", saved.Translation.Claude.Model);
        window.Close();
    });

    [Fact]
    public void A_provider_this_window_does_not_offer_survives_an_unrelated_save_and_a_pick_replaces_it() => OnStaThread(() =>
    {
        // A local LLM through Ollama (set by hand: this window offers only Free and Claude).
        var settings = new AppSettings();
        settings.Translation.Provider = ProviderIds.Compat;
        var window = new SettingsWindow(settings, () => null, () => { });

        window.HkClear.Text = "Ctrl+Shift+X";
        Assert.True(window.TryApply());
        Assert.Equal(ProviderIds.Compat, settings.Translation.Provider);

        window.Provider.SelectedIndex = 1;
        Assert.True(window.TryApply());
        Assert.Equal(ProviderIds.Claude, settings.Translation.Provider);
        window.Close();
    });

    [Fact]
    public void Typed_key_replaces_the_stored_one() => OnStaThread(() =>
    {
        var settings = new AppSettings();
        settings.Translation.Claude.ApiKeyProtected = ForeignBlob;
        var window = new SettingsWindow(settings, () => null, () => { });

        window.ClaudeKey.Password = "  sk-ant-typed  ";
        Assert.True(window.TryApply());

        Assert.Equal("sk-ant-typed", settings.Translation.Claude.ApiKey);
        window.Close();
    });
}
