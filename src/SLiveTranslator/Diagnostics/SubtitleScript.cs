using System;
using System.Collections.Generic;
using System.Linq;

namespace SLiveTranslator.Diagnostics;

/// <summary>One subtitle as the game shows it. Frames are the readings shown in turn, each for one poll.</summary>
internal sealed record SubtitleShot(string Expected, List<string> Frames, float Size);

/// <summary>A scripted conversation for <see cref="SubtitleReplay"/>: what the game shows, poll by poll.</summary>
internal sealed class SubtitleScript
{
    static readonly float[] Sizes = [28f, 25f, 32f, 30f, 26f, 29f];

    /// <summary>null = no subtitle for a poll.</summary>
    public List<SubtitleShot?> Shots { get; } = new();

    /// <summary>Shot index → extra silent time before it, in ms.</summary>
    public Dictionary<int, int> WaitBefore { get; } = new();

    float NextSize() => Sizes[Shots.Count(s => s is not null) % Sizes.Length];

    public SubtitleScript Say(string text, int frames = 5, float? size = null)
    {
        Shots.Add(new SubtitleShot(text, Enumerable.Repeat(text, frames).ToList(), size ?? NextSize()));
        return this;
    }

    /// <summary>Words appear a few at a time, then the full line stays.</summary>
    public SubtitleScript Typewriter(string text, int steps = 3, int fullFrames = 4)
    {
        var words = text.Split(' ');
        var frames = Enumerable.Range(1, steps).Select(i => string.Join(" ", words.Take(Math.Max(1, words.Length * i / (steps + 1))))).ToList();
        frames.AddRange(Enumerable.Repeat(text, fullFrames));
        Shots.Add(new SubtitleShot(text, frames, NextSize()));
        return this;
    }

    /// <summary>The line is shown, then a last word is added to it.</summary>
    public SubtitleScript Append(string text, string tail, int frames = 4)
    {
        var frameTexts = Enumerable.Repeat(text, frames).Concat(Enumerable.Repeat(text + tail, frames)).ToList();
        Shots.Add(new SubtitleShot(text + tail, frameTexts, NextSize()));
        return this;
    }

    public SubtitleScript Gap(int polls = 4, int extraMs = 0)
    {
        for (int i = 0; i < polls; i++) Shots.Add(null);
        if (extraMs > 0) WaitBefore[Shots.Count] = extraMs;
        return this;
    }

    /// <summary>
    /// A Witcher-style conversation over a moving scene: typewriter reveals, appended words, lines wider than the
    /// region, apostrophes, a Yes/No swap, a repeat after a pause, two-line sentences and numbered choices.
    /// </summary>
    public static SubtitleScript Conversation() => new SubtitleScript()
        .Typewriter("Geralt: The griffin has been hunting near the old mill for weeks.")
        .Say("Three cows and the miller's boy, sir. Gone, all of them.")
        .Say("Did anyone see where it nests?")
        .Say("Yes.")
        .Say("No.")
        .Say("Well, make up your mind, man.")
        .Append("Well, yes, but... that book was horribly", " dull!")
        .Say("You promised Ciri you'd train with her, and a witcher keeps his word even when the weather turns foul.")
        .Say("Won't you stay a while? I'm sure she'd want you to.")
        .Say("You should've asked before you took it. It's not yours.")
        .Gap()
        .Say("They'll come at dawn, and we're not ready.")
        .Say("Go and train with her. Then come back,\nit'll give me a chance to put my face on.")
        .Say("Where is Yennefer?")
        .Gap(4, extraMs: 3_000)
        .Say("Where is Yennefer?")
        .Say("Don't worry, it'll be fine. Ciri's waiting at the gate.")
        .Typewriter("The road to Novigrad is long, and the bandits know every bend of it.")
        .Say("Hmm. So you did read the chapter after all.")
        .Say("Still, you should've asked if you could borrow the book.")
        .Say("Why the hell not? The girl refuses to do as she's told.")
        .Gap()
        .Say("1. I'll stay a little longer.\n2. You're right, I should go see Ciri.")
        .Say("Take a trophy from the griffin's corpse.")
        .Say("Vesemir taught me everything I know about monsters.")
        .Append("We'll leave at first light, before the", " fog lifts.")
        .Say("Ghouls feed on the dead. Necrophages, all of them.")
        .Say("I've never seen a wraith this strong in daylight.")
        .Say("Roach needs new shoes before we ride to Oxenfurt.")
        .Say("That's the second contract this week, and nobody has paid me yet.")
        .Gap()
        .Say("Who's there? Show yourself!")
        .Say("Easy. I'm only here for the notice board.")
        .Typewriter("The alderman says the beast came from the swamp beyond the crossroads.")
        .Say("Couldn't you have waited until morning?")
        .Say("There's a storm coming. We'd best find shelter.")
        .Say("Lambert, put down the bottle and help me with this.")
        .Say("Eskel's gone to the mountains again, hasn't he?")
        .Gap()
        .Say("It's an old elven ruin, older than the city itself.")
        .Say("If you're lying to me, I'll know.")
        .Say("A long and complicated story, one we haven't the time for now,\nso I'll tell it to you on the road.")
        .Say("What's that smell? Something's rotting under the floor.")
        .Say("Wouldn't be the first time a witcher got paid in onions.")
        .Append("Keep your sword close and your", " silver closer.")
        .Gap()
        .Say("We should've left when we had the chance.")
        .Say("Triss? What are you doing in Velen?")
        .Say("Long story. I'll explain later, I promise.")
        .Say("Thank you, Geralt. I won't forget this.")
        .Say("Farewell, then. And watch your back.")
        .Gap();

    /// <summary>A still camera: lines replace each other with no gap, and short answers change only a few pixels.</summary>
    public static SubtitleScript StillConversation() => new SubtitleScript()
        .Say("Did anyone see where it nests?")
        .Say("Yes.")
        .Say("No.")
        .Say("Three cows and the miller's boy, sir. Gone, all of them.")
        .Say("Never.")
        .Say("Where is Yennefer?")
        .Say("She left for Vengerberg.")
        .Say("A long and complicated story, one we haven't the time for now,\nso I'll tell it to you on the road.")
        .Say("Fine.")
        .Say("Wouldn't be the first time a witcher got paid in onions.")
        .Say("Hurry!")
        .Say("Then we ride at dawn.")
        // Each pair shares most of its letters; only one word differs.
        .Say("Where is the griffin?")
        .Say("Where is the wyvern?")
        .Say("What do you want?")
        .Say("What do you need?")
        .Gap();

    /// <summary>One line that stays on screen for ten seconds: OCR should run about once a second, not every poll.</summary>
    public static SubtitleScript Hold() => new SubtitleScript()
        .Say("You promised Ciri you'd train with her, and a witcher keeps his word.", frames: 34)
        .Gap();

    /// <summary>Ten seconds with no subtitle.</summary>
    public static SubtitleScript Silence() => new SubtitleScript().Gap(34);
}
