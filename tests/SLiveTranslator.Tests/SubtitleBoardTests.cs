using System.Linq;
using SLiveTranslator.UI;
using Xunit;

namespace SLiveTranslator.Tests;

public class SubtitleBoardTests
{
    [Fact]
    public void A_new_line_waits_for_its_translation()
    {
        var board = new SubtitleBoard();

        board.Detected(1, "Where is the griffin?", false);
        Assert.Equal([new SubtitleLineView("…", true)], board.Visible(3));

        board.Translated(1, "Con griffin ở đâu?", false, 3);
        Assert.Equal([new SubtitleLineView("Con griffin ở đâu?", false)], board.Visible(3));
    }

    [Fact]
    public void A_line_that_grew_keeps_its_old_translation_until_the_new_one_arrives()
    {
        var board = new SubtitleBoard();
        board.Detected(1, "Well, yes, but... that book was horribly", false);
        board.Translated(1, "Ừ, nhưng... cuốn sách đó thật kinh khủng", false, 3);

        board.Detected(1, "Well, yes, but... that book was horribly dull!", false);
        Assert.Equal([new SubtitleLineView("Ừ, nhưng... cuốn sách đó thật kinh khủng", false)], board.Visible(3)); // no flash back to "…"

        board.Translated(1, "Ừ, nhưng... cuốn sách đó chán kinh khủng!", false, 3);
        Assert.Equal("Ừ, nhưng... cuốn sách đó chán kinh khủng!", board.Visible(3).Single().Text);
        Assert.Equal("Well, yes, but... that book was horribly dull!", board.History(12).Single().Original);
    }

    [Fact]
    public void A_failed_line_shows_its_original_dimmed_with_a_retry_mark_until_a_translation_arrives()
    {
        var board = new SubtitleBoard();
        board.Detected(1, "Where is the griffin?", false);

        board.Failed(1);
        Assert.Equal([new SubtitleLineView("Where is the griffin? ↻", false, Failed: true)], board.Visible(3));
        Assert.Equal("Where is the griffin? ↻", board.History(12).Single().Translated);

        board.Translated(1, "Con griffin ở đâu?", false, 3);
        Assert.Equal([new SubtitleLineView("Con griffin ở đâu?", false)], board.Visible(3));
    }

    [Fact]
    public void A_line_that_is_not_language_is_dropped_from_the_box_and_the_history()
    {
        var board = new SubtitleBoard();
        board.Detected(1, "Where is the griffin?", false);
        board.Translated(1, "Con griffin ở đâu?", false, 3);
        board.Detected(2, "Ilrn ooe", false);
        board.Detected(3, "Out by the old mill.", false);

        Assert.False(board.Translated(2, "", false, 3));

        Assert.Equal([new SubtitleLineView("Con griffin ở đâu?", false), new SubtitleLineView("…", true)], board.Visible(3));
        Assert.Equal(["Where is the griffin?", "Out by the old mill."], board.History(12).Select(h => h.Original));
        Assert.Equal([1, 3], board.Records(12).Select(r => r.Id));
    }

    [Fact]
    public void Dropping_a_line_of_an_earlier_conversation_keeps_the_box_on_the_current_one()
    {
        var board = new SubtitleBoard();
        board.Detected(1, "Ilrn ooe", false);
        board.Detected(2, "Where is the griffin?", false);
        board.CloseWindow();
        board.Detected(3, "Out by the old mill.", false);

        board.Translated(1, "", false, 3);

        Assert.Equal([new SubtitleLineView("…", true)], board.Visible(3));
    }

    [Fact]
    public void A_low_confidence_line_is_marked()
    {
        var board = new SubtitleBoard();

        board.Detected(1, "Wh@t do you want, witcher?", true);
        Assert.True(board.Visible(3).Single().LowConfidence); // "…" while it is translated
        board.Translated(1, "Ngươi muốn gì, witcher?", true, 3);

        Assert.True(board.Visible(3).Single().LowConfidence);
    }

    [Fact]
    public void The_translation_of_a_garbled_reading_stays_marked_until_the_clean_reading_is_translated()
    {
        var board = new SubtitleBoard();
        board.Detected(1, "Wh@t d0 y0u w@nt, witcher?", true);
        board.Translated(1, "Ngươi mu0n gì, witcher?", true, 3);

        board.Detected(1, "What do you want, witcher?", false);
        Assert.Equal([new SubtitleLineView("Ngươi mu0n gì, witcher?", false, LowConfidence: true)], board.Visible(3));

        board.Translated(1, "Ngươi muốn gì, witcher?", false, 3);
        Assert.Equal([new SubtitleLineView("Ngươi muốn gì, witcher?", false)], board.Visible(3));
    }

    [Fact]
    public void A_late_translation_brings_its_line_back_into_the_box()
    {
        var board = new SubtitleBoard();
        board.Detected(1, "Where is the griffin?", false);
        board.Detected(2, "Out by the old mill.", false);
        board.Translated(1, "Con griffin ở đâu?", false, 3);
        board.CloseWindow(); // the subtitle cleared and the linger ended
        Assert.Empty(board.Visible(3));

        Assert.True(board.Translated(2, "Ở ngoài cối xay cũ.", false, 3));

        Assert.Equal(["Ở ngoài cối xay cũ."], board.Visible(3).Select(v => v.Text));
    }

    [Fact]
    public void A_late_translation_of_a_line_older_than_the_box_goes_to_the_history_only()
    {
        var board = new SubtitleBoard();
        for (int id = 1; id <= 4; id++) board.Detected(id, $"Line {id}.", false);
        board.CloseWindow();
        board.Detected(5, "A new conversation.", false);

        Assert.False(board.Translated(1, "Dòng 1.", false, 3)); // not among the last 3
        Assert.True(board.Translated(3, "Dòng 3.", false, 3)); // still among the last 3: shown with the newer lines

        Assert.Equal(["Dòng 3.", "…", "…"], board.Visible(3).Select(v => v.Text));
        Assert.Equal("Dòng 1.", board.History(12).First().Translated);
    }

    [Fact]
    public void The_box_shows_the_configured_number_of_lines_and_old_entries_are_dropped()
    {
        var board = new SubtitleBoard(maxEntries: 5);
        for (int id = 1; id <= 8; id++) board.Detected(id, $"Line {id}.", false);

        Assert.Equal(5, board.Count);
        Assert.Equal(2, board.Visible(2).Count);
        Assert.Equal(["Line 4.", "Line 5.", "Line 6.", "Line 7.", "Line 8."], board.History(12).Select(h => h.Original));
    }

    [Fact]
    public void Lines_expose_their_source_translation_and_provider_for_a_hand_fix()
    {
        var board = new SubtitleBoard();
        board.Detected(1, "Roach!", false);
        board.Translated(1, "Cá viền đỏ!", false, 3, "free-mt");
        board.Detected(2, "Where is the griffin?", false);

        Assert.Equal([new SubtitleRecord(1, "Roach!", "Cá viền đỏ!", "free-mt"), new SubtitleRecord(2, "Where is the griffin?", null, null)], board.Records(12));
    }

    [Fact]
    public void A_hand_fix_shows_on_every_line_with_that_source()
    {
        var board = new SubtitleBoard();
        board.Detected(1, "Roach!", true);
        board.Translated(1, "Cá viền đỏ!", true, 3, "free-mt");
        board.Detected(2, "Where is she?", false);
        board.Detected(3, "Roach!", false);
        board.Failed(3);

        Assert.True(board.Fix("Roach!", "Roach!"));

        Assert.Equal([new SubtitleLineView("Roach!", false), new SubtitleLineView("…", true), new SubtitleLineView("Roach!", false)], board.Visible(3));
        Assert.Equal(["user", null, "user"], board.Records(12).Select(r => r.Provider));
        Assert.False(board.Fix("Not shown.", "x"));
    }
}
