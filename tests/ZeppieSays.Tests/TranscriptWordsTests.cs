using System.IO;
using ZeppieSays.Core;
using ZeppieSays.Engines;
using ZeppieSays.Notes;
using ZeppieSays.Services;

namespace ZeppieSays.Tests;

public class TranscriptWordsTests
{
    [Fact]
    public void Joins_Whisper_tokens_into_words()
    {
        // Whisper marks the start of a word with a space; "Lu" + "mora" is one word.
        var words = TimedWords.FromTokens([(" Ik", 0.0, 0.2), (" werk", 0.2, 0.5), (" bij", 0.5, 0.6), (" Lu", 0.6, 0.8), ("mora", 0.8, 1.0), (".", 1.0, 1.0)]);
        Assert.Equal(["Ik", "werk", "bij", "Lumora."], words.Select(w => w.Text));
        Assert.Equal((0.6, 1.0), (words[3].Start, words[3].End));
    }

    [Fact]
    public void Joins_SentencePiece_tokens_into_words()
    {
        var words = TimedWords.FromTokens([("▁Hal", 0.0, 0.1), ("lo", 0.1, 0.3), ("▁Sam", 0.3, 0.6)]);
        Assert.Equal(["Hallo", "Sam"], words.Select(w => w.Text));
    }

    [Fact]
    public void Hands_each_word_to_the_turn_it_was_said_in()
    {
        List<SpokenWord> words =
        [
            new("Hoe", 10.2, 10.4), new("werkt", 10.4, 10.7), new("dat?", 10.7, 11.0),
            new("Hij", 11.2, 11.4), new("pakt", 11.4, 11.6), new("het", 11.6, 11.8), new("geluid.", 11.8, 12.3),
            new("Oké.", 16.1, 16.5),
        ];
        // Before: one long paragraph. Now the detection found Sam asking and Dirk answering.
        var paragraphs = TranscriptWords.ToParagraphs(
            [(0, 10.0, 11.1, "Sam"), (0, 11.1, 15.0, "Dirk"), (0, 16.0, 17.0, "Sam")], words, text => text);

        Assert.Equal(
            [new TranscriptParagraph(10.0, "Sam", "Hoe werkt dat?"), new(11.1, "Dirk", "Hij pakt het geluid."), new(16.0, "Sam", "Oké.")],
            paragraphs);
    }

    [Fact]
    public void A_word_outside_every_turn_goes_to_the_nearest_on_its_own_track()
    {
        List<SpokenWord> words = [new("ja", 5.0, 5.2, Track: 1), new("nee", 5.0, 5.2, Track: 0)];
        var paragraphs = TranscriptWords.ToParagraphs(
            [(0, 0.0, 1.0, "Dirk"), (1, 8.0, 9.0, "Anna")], words, text => text);

        Assert.Equal([new TranscriptParagraph(0.0, "Dirk", "nee"), new(8.0, "Anna", "ja")], paragraphs);
    }

    [Fact]
    public void Cleans_each_paragraph_like_the_first_transcription()
    {
        var paragraphs = TranscriptWords.ToParagraphs([(0, 0.0, 2.0, "Sam")], [new("uh", 0, 0.2), new("hallo", 0.3, 0.6)],
            text => TextShaping.Clean(text, new AppSettings(), ScreenContext.Empty));
        Assert.Equal("Hallo", paragraphs.Single().Text);
    }

    [Fact]
    public void Words_survive_a_save()
    {
        var markdown = Path.Combine(Directory.CreateTempSubdirectory().FullName, "t.md");
        new TranscriptWords { SpeechModel = "Whisper large-v3", Words = [new("Hallo", 1.5, 1.9, 1)] }.Save(markdown);

        var again = TranscriptWords.Load(markdown)!;
        Assert.Equal("Whisper large-v3", again.SpeechModel);
        Assert.Equal(new SpokenWord("Hallo", 1.5, 1.9, 1), again.Words.Single());
        Assert.Null(TranscriptWords.Load(Path.ChangeExtension(markdown, ".other.md")));
    }

    [Fact]
    public void Keeping_the_text_needs_the_words()
    {
        var options = NoteOptions.From(new NotetakerSettings { VoiceModel = "titanet" });
        Assert.Equal("titanet", options.VoiceModel);

        options.KeepText = true;
        Assert.False(options.KeepText); // no words: transcribing again is the only way
        Assert.True(options.TranscribeAgain);

        options.CanKeepText = true;
        options.KeepText = true;
        Assert.False(options.TranscribeAgain);
        options.CanKeepText = false;
        Assert.False(options.KeepText);
    }
}
