using System.Collections.Generic;
using System.Text;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The fix-rule half of the profile matching contract, with the reference validator's cases.</summary>
public class FixRuleApplierTests
{
    static readonly FixRuleApplier Witcher = new(TestProfiles.Witcher3().FixRules);

    static FixRuleApplier Rules(params FixRule[] rules) => new(rules);

    // validate-profile.js, ruleCases, run on the shipped profile (its guarded rules included)
    [Theory]
    [InlineData("The witch burned in the square.", "Phù thủy bị thiêu ở quảng trường.", "Phù thủy bị thiêu ở quảng trường.")]
    // fix rules alone; in the app the keep placeholder protects "witcher" before MT
    [InlineData("A witcher and a sorceress.", "Một phù thủy và một nữ phù thủy.", "Một phù thủy và một nữ pháp sư.")]
    [InlineData("Witch hunters hate witchers.", "Thợ săn phù thủy ghét các phù thủy.", "Thợ săn phù thủy ghét các phù thủy.")]
    [InlineData("Witch hunters burn sorceresses.", "Thợ săn phù thủy thiêu các nữ phù thủy.", "Thợ săn phù thủy thiêu các nữ pháp sư.")]
    [InlineData("Sorceress, your witchcraft won't help you.", "Nữ phù thủy, phép phù thủy của cô không giúp được cô đâu.", "Nữ pháp sư, phép phù thủy của cô không giúp được cô đâu.")]
    [InlineData("A witcher? The Crones would eat you alive.", "Một phù thủy? Những mụ phù thủy sẽ ăn tươi nuốt sống anh.", "Một phù thủy? Những mụ phù thủy sẽ ăn tươi nuốt sống anh.")]
    [InlineData("Maybe for a sorceress.", "Có lẽ đối với một nữ phù thuỷ.", "Có lẽ đối với một nữ pháp sư.")]
    [InlineData("A vampire, not a ghoul.", "Một ma cà rồng, không phải ghoul.", "Một ma cà rồng, không phải ghoul.")]
    [InlineData("He drowned. Drowners got him.", "Anh ta chết đuối. Chết đuối đã bắt anh ta.", "Anh ta chết đuối. Chết đuối đã bắt anh ta.")]
    [InlineData("Triss?", "Triss?", "Triss?")]
    [InlineData("Triss?", "Tris!", "Triss!")]
    [InlineData("ROACH", "CÁ VIỀN ĐỎ", "ROACH")]
    [InlineData("There's a roach in the stew.", "Có một con gián trong món hầm.", "Có một con gián trong món hầm.")]
    [InlineData("Take a trophy from the griffin.", "Lấy một chiếc cúp từ xác con griffin.", "Lấy một chiến lợi phẩm từ xác con griffin.")]
    [InlineData("WITCHER SENSES - IMPORTANT OBJECTS", "CÁC GIÁC QUAN PHÙ THỦY - ĐỐI TƯỢNG QUAN TRỌNG", "GIÁC QUAN WITCHER - ĐỐI TƯỢNG QUAN TRỌNG")]
    [InlineData("Use your Witcher Senses.", "Sử dụng các giác quan phù thủy của bạn.", "Sử dụng Giác quan Witcher của bạn.")]
    [InlineData("The Wild Hunt appeared.", "Cuộc đi săn hoang dã xuất hiện.", "Wild Hunt xuất hiện.")]
    [InlineData("Elder Blood", "Máu Già", "Dòng Máu Cổ Xưa")]
    [InlineData("Two hundred crowns.", "Hai trăm vương miện.", "Hai trăm crown.")]
    // The validator expected "phù thủy" here: the guarded witcher rule merged from fixrules-pending.json now fixes it.
    [InlineData("I have no inventory left, witcher.", "Tôi không còn hàng tồn kho, phù thủy.", "Tôi không còn hàng tồn kho, witcher.")]
    [InlineData("INVENTORY", "HÀNG TỒN KHO", "TÚI ĐỒ")]
    [InlineData("But you were asleep, Uncle Vesemir.", "Nhưng chú đã ngủ rồi, chú Vesemir.", "Nhưng chú đã ngủ rồi, chú Vesemir.")]
    [InlineData("I'm very sorry, Uncle Vesemir.", "Tôi rất xin lỗi, anh Vesemir.", "Tôi rất xin lỗi, chú Vesemir.")]
    [InlineData("Space Continue", "Không gian tiếp tục", "[Space] Tiếp tục")]
    [InlineData("Space Continue", "Space Tiếp tục", "[Space] Tiếp tục")]
    [InlineData("Space Skip", "Space bỏ qua", "[Space] Bỏ qua")]
    [InlineData("Bald Mountain", "Núi Hói", "Núi Trọc")]
    [InlineData("You may resume.", "Bạn có thể sơ yếu lý lịch.", "Bạn có thể sơ yếu lý lịch.")]
    [InlineData("Cast Sign", "Dấu đúc", "Dùng Ấn")]
    [InlineData("Quest", "Sứ mệnh", "Nhiệm vụ")]
    [InlineData("My quest is my calling.", "Sứ mệnh của tôi là tiếng gọi của tôi.", "Sứ mệnh của tôi là tiếng gọi của tôi.")]
    [InlineData("Place of Power", "Nơi quyền lực", "Điểm Linh Lực")]
    [InlineData("Geralt of Rivia", "Geralt của Rivia", "Geralt xứ Rivia")]
    public void The_reference_cases_come_out_as_reviewed(string source, string translation, string expected) =>
        Assert.Equal(expected, Witcher.Apply(source, translation));

    // The guards of the rules merged from fixrules-pending.json
    [Theory]
    [InlineData("Roach found a cockroach.", "Roach tìm thấy một con gián.", "Roach tìm thấy một con gián.")]
    [InlineData("Roach, stay.", "Con gián, ở lại.", "Roach, ở lại.")]
    [InlineData("A medal for Geralt's medallion?", "Một huy chương cho huy chương của Geralt?", "Một huy chương cho huy chương của Geralt?")]
    [InlineData("The king lost his crowns.", "Nhà vua mất vương miện.", "Nhà vua mất vương miện.")]
    [InlineData("The joust champion's trophy.", "Chiếc cúp của nhà vô địch.", "Chiếc cúp của nhà vô địch.")]
    [InlineData("Take the trophy.", "Lấy cúp.", "Lấy chiến lợi phẩm.")]
    [InlineData("Kill the ghouls.", "Giết những con ma cà rồng.", "Giết những con ghoul.")]
    [InlineData("A sorcerer!", "Một phù thủy!", "Một pháp sư!")]
    [InlineData("A sorcerer and a witch.", "Một phù thủy và một phù thủy.", "Một phù thủy và một phù thủy.")]
    [InlineData("Drowners in the lake.", "Người chết đuối trong hồ.", "Con drowner trong hồ.")]
    [InlineData("A drowner drowned him.", "Một người chết đuối đã dìm chết anh ta.", "Một người chết đuối đã dìm chết anh ta.")]
    public void A_merged_rule_is_skipped_when_its_unless_word_is_in_the_line(string source, string translation, string expected) =>
        Assert.Equal(expected, Witcher.Apply(source, translation));

    [Fact]
    public void Triggers_and_unless_words_follow_the_case_rule()
    {
        var rules = Rules(
            new FixRule { Trigger = "Quest", Wrong = "sứ mệnh", Right = "nhiệm vụ" },
            new FixRule { Trigger = "drowner", Wrong = "chết đuối", Right = "drowner", Unless = ["Lake"] });

        Assert.Equal("nhiệm vụ", rules.Apply("QUEST", "sứ mệnh")); // capitalized trigger: as written or ALL CAPS
        Assert.Equal("sứ mệnh", rules.Apply("quest", "sứ mệnh"));
        Assert.Equal("drowner", rules.Apply("DROWNER", "chết đuối")); // lowercase trigger: any case
        Assert.Equal("chết đuối", rules.Apply("A drowner in the LAKE", "chết đuối")); // "Lake": as written or ALL CAPS
        Assert.Equal("drowner", rules.Apply("A drowner in the lake", "chết đuối"));
    }

    [Fact]
    public void Wrong_text_matches_whole_syllables_in_either_tone_placement_and_any_case()
    {
        var rules = Rules(new FixRule { Trigger = "sorceress", Wrong = "phù thủy", Right = "pháp sư" });

        Assert.Equal("nữ pháp sư", rules.Apply("sorceress", "nữ phù thuỷ")); // old placement in the answer
        Assert.Equal("NỮ PHÁP SƯ", rules.Apply("sorceress", "NỮ PHÙ THỦY")); // ALL CAPS carries over
        Assert.Equal("Pháp sư!", rules.Apply("sorceress", "Phù   thủy!")); // a capital carries over; any white space
        Assert.Equal("phù thủyy", rules.Apply("sorceress", "phù thủyy")); // not a whole syllable
        string decomposed = "nữ phù thủy".Normalize(NormalizationForm.FormD);
        Assert.Equal("nữ pháp sư", rules.Apply("sorceress", decomposed));
        Assert.Equal("òa", FixRuleApplier.FoldTone("oà"));
        Assert.Equal("THỦY", FixRuleApplier.FoldTone("THUỶ"));
    }

    [Fact]
    public void Every_match_is_replaced_and_each_rule_runs_once_in_file_order()
    {
        var fired = new List<int>();
        var rules = Rules(
            new FixRule { Trigger = "Roach", Wrong = "cá viền đỏ", Right = "Roach" },
            new FixRule { Trigger = "Roach", Wrong = "Roach và", Right = "Roach với" }); // sees the first rule's result

        Assert.Equal("Roach với Roach", rules.Apply("Roach and Roach", "cá viền đỏ và cá viền đỏ", fired));
        Assert.Equal([0, 1], fired);
    }

    [Fact]
    public void A_rule_whose_trigger_is_missing_changes_nothing()
    {
        var fired = new List<int>();

        Assert.Equal("Có một con gián trong món hầm.", Witcher.Apply("There's a roach in the stew.", "Có một con gián trong món hầm.", fired));
        Assert.Empty(fired);
    }
}
