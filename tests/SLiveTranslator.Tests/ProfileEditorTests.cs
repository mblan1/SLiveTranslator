using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using SLiveTranslator.UI;
using SLiveTranslator.UI.Pages;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The Game profile page's grids: edits go through <see cref="GameProfileStore"/> in a temp folder and come back from the file.</summary>
public class ProfileEditorTests
{
    static (FakeHost Host, SettingsViewModel Vm, FakePrompts Prompts) Make(TempDir dir)
    {
        var host = new FakeHost(dir.File("Profiles"));
        var vm = new SettingsViewModel(host, new FakeScheduler().Delay);
        var prompts = new FakePrompts();
        vm.Prompts = prompts;
        return (host, vm, prompts);
    }

    [Fact]
    public void Grid_edits_are_saved_through_the_store_and_read_back_from_the_file()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("Profiles"));
        string seeded = Encoding.UTF8.GetString(TestProfiles.Witcher3Bytes).Replace("\"version\": 1,", "\"version\": 1, \"futureField\": { \"a\": 1 },");
        File.WriteAllText(Path.Combine(dir.File("Profiles"), "witcher3.json"), seeded);
        var (host, vm, _) = Make(dir);
        var editor = vm.Profiles;
        var inUse = host.Profiles.Find("witcher3")!;
        Assert.Same(inUse, editor.EditTarget);

        editor.Keep.AddCommand.Execute(null);
        editor.Keep.Selected!.Text = "Jaskier";
        editor.Glossary.Items.Single(g => g.En == "Witcher Senses").Vi = "Giác quan của witcher";
        editor.Characters.Selected = editor.Characters.Items.Single(c => c.Name == "Geralt");
        editor.Characters.Selected!.Address.AddCommand.Execute(null);
        var pair = editor.Characters.Selected!.Address.Selected!;
        (pair.To, pair.Self, pair.Other) = ("Jaskier", "tôi", "cậu");
        editor.FixRules.Items[0].Unless = "red mullet, fish";
        editor.Glossary.AddCommand.Execute(null); // a blank row is left out
        editor.Name = "The Witcher 3";

        Assert.True(editor.IsDirty);
        Assert.False(editor.CanSwitch);
        Assert.True(vm.HasChanges);
        Assert.DoesNotContain("Jaskier", inUse.Keep); // the profile in use is not touched before the save
        Assert.True(vm.Save());

        Assert.False(editor.IsDirty);
        Assert.False(vm.HasChanges);
        Assert.Empty(host.Saves); // only the profile changed, not the settings
        var stored = host.Profiles.Find("witcher3")!;
        Assert.NotSame(inUse, stored);
        Assert.Equal(1, inUse.Version);
        Assert.Single(host.Profiles.Profiles, p => p.Id == "witcher3");

        var reread = new GameProfileStore(dir.File("Profiles")).Find("witcher3")!;
        Assert.Equal(2, reread.Version);
        Assert.Equal("The Witcher 3", reread.Name);
        Assert.Equal("Jaskier", reread.Keep[^1]);
        Assert.Equal(TestProfiles.Witcher3().Keep.Count + 1, reread.Keep.Count);
        Assert.Equal("Giác quan của witcher", reread.Glossary.Single(g => g.En == "Witcher Senses").Vi);
        Assert.Equal(TestProfiles.Witcher3().Glossary.Count, reread.Glossary.Count);
        var geralt = reread.Characters.Single(c => c.Name == "Geralt");
        Assert.Contains(geralt.Address, a => (a.To, a.Self, a.Other) == ("Jaskier", "tôi", "cậu"));
        Assert.Equal(["red mullet", "fish"], reread.FixRules[0].Unless);
        Assert.Equal(1, reread.Unknown!["futureField"].GetProperty("a").GetInt32());
        Assert.Single(Directory.GetFiles(dir.File("Profiles"), "*.json"), f => !Path.GetFileName(f).StartsWith('.'));
    }

    [Fact]
    public void An_edit_typed_back_is_no_change_and_discard_drops_the_edits()
    {
        using var dir = new TempDir();
        var (_, vm, _) = Make(dir);
        var editor = vm.Profiles;
        var row = editor.Keep.Items[0];
        string text = row.Text;

        row.Text = text + "x";
        Assert.True(editor.IsDirty);
        row.Text = text;
        Assert.False(editor.IsDirty);

        editor.Keep.Selected = row;
        editor.Keep.RemoveCommand.Execute(null);
        Assert.True(vm.HasChanges);
        vm.Discard();
        Assert.False(vm.HasChanges);
        Assert.Equal(text, editor.Keep.Items[0].Text);
    }

    [Fact]
    public void The_profile_in_use_is_a_setting_and_a_new_profile_gets_its_own_file()
    {
        using var dir = new TempDir();
        var (host, vm, _) = Make(dir);
        var editor = vm.Profiles;

        Assert.Equal(["", "none", "witcher3"], editor.UseChoices.Select(c => c.Value));
        editor.SelectedUse = editor.UseChoices.Single(c => c.Value == "none");
        Assert.Equal(SettingsChange.Translation, vm.PendingChanges);

        editor.NewCommand.Execute(null);

        Assert.Equal("custom", editor.EditTarget!.Id);
        Assert.Equal("New profile", editor.Name);
        Assert.True(File.Exists(Path.Combine(dir.File("Profiles"), "custom.json")));
        Assert.Contains(editor.UseChoices, c => c.Value == "custom");
        Assert.True(vm.Save());
        Assert.Equal("none", host.Saves.Single().Settings.Translation.ProfileId);
    }

    [Fact]
    public async Task New_and_import_leave_a_file_that_could_not_be_read_alone()
    {
        using var dir = new TempDir();
        string profiles = dir.File("Profiles");
        Directory.CreateDirectory(profiles);
        const string typo = "{ \"id\": \"custom\", \"name\": \"Mine\" ";
        File.WriteAllText(Path.Combine(profiles, "custom.json"), typo);
        var (host, vm, prompts) = Make(dir);

        vm.Profiles.NewCommand.Execute(null);
        Assert.Equal("custom-2", vm.Profiles.EditTarget!.Id);

        var imported = new GameProfile { Id = "gwent", Name = "Gwent" };
        File.WriteAllText(Path.Combine(profiles, "gwent.json"), "not a profile yet");
        prompts.OpenFile = dir.File("gwent-share.json");
        File.WriteAllBytes(prompts.OpenFile, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(imported, GameProfileStore.JsonOptions));
        await vm.Profiles.ImportCommand.ExecuteAsync(null);

        Assert.Equal(typo, File.ReadAllText(Path.Combine(profiles, "custom.json")));
        Assert.Equal("not a profile yet", File.ReadAllText(Path.Combine(profiles, "gwent.json")));
        Assert.Equal("Gwent", host.Profiles.Find("gwent")!.Name);
        Assert.Equal(["custom-2", "gwent", "witcher3"], new GameProfileStore(profiles).Profiles.Select(p => p.Id).Order());
    }

    [Fact]
    public async Task An_imported_profile_replaces_the_one_with_its_id_after_asking_and_export_writes_the_grids()
    {
        using var dir = new TempDir();
        var (host, vm, prompts) = Make(dir);
        var editor = vm.Profiles;
        var imported = TestProfiles.Witcher3();
        imported.Keep = ["Geralt", "Ciri"];
        string file = dir.File("shared.json");
        File.WriteAllBytes(file, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(imported, GameProfileStore.JsonOptions));
        prompts.OpenFile = file;

        prompts.Confirm = false;
        await editor.ImportCommand.ExecuteAsync(null);
        Assert.Equal(TestProfiles.Witcher3().Keep.Count, host.Profiles.Find("witcher3")!.Keep.Count);

        prompts.Confirm = true;
        await editor.ImportCommand.ExecuteAsync(null);
        Assert.Equal(["Geralt", "Ciri"], host.Profiles.Find("witcher3")!.Keep);
        Assert.Equal(2, host.Profiles.Find("witcher3")!.Version);
        Assert.Equal(["Geralt", "Ciri"], editor.Keep.Items.Select(k => k.Text));

        editor.Keep.Items[1].Text = "Cirilla";
        prompts.SaveFile = dir.File("export.json");
        editor.ExportCommand.Execute(null);
        Assert.Equal(["Geralt", "Cirilla"], GameProfileStore.Parse(File.ReadAllBytes(prompts.SaveFile)).Keep);
        Assert.True(editor.IsDirty); // exported, not saved
    }

    [Fact]
    public void A_file_that_is_no_profile_is_not_imported()
    {
        using var dir = new TempDir();
        var (host, vm, prompts) = Make(dir);
        File.WriteAllText(dir.File("broken.json"), "{ not json");
        prompts.OpenFile = dir.File("broken.json");

        vm.Profiles.ImportCommand.Execute(null);

        Assert.Equal(MessageKind.Error, prompts.Messages.Single().Kind);
        Assert.StartsWith("broken.json could not be imported", prompts.Messages.Single().Text);
        Assert.Single(host.Profiles.Profiles);
    }
}
