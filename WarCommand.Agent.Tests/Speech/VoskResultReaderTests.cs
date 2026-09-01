using WarCommand.Agent.Speech.Recognition;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// The recognizer's JSON is where a per-token confidence either survives or is quietly replaced by
/// an utterance-level one. It is the number request_points.confidence stores, so it is read here
/// and never derived.
/// </summary>
public class VoskResultReaderTests
{
    [Fact]
    public void Word_confidences_are_read_per_token()
    {
        var utterance = VoskResultReader.Read(
            """
            {"result":[
              {"conf":0.99,"start":0.3,"end":0.7,"word":"mortar"},
              {"conf":0.62,"start":0.8,"end":1.0,"word":"eight"},
              {"conf":0.41,"start":1.0,"end":1.2,"word":"five"}],
             "text":"mortar eight five"}
            """);

        Assert.Equal("mortar eight five", utterance.Text);
        Assert.Equal(0.99, utterance.Tokens[0].Confidence, 6);
        Assert.Equal(0.41m, utterance.MinDigitConfidence);
    }

    [Fact]
    public void The_utterance_score_is_the_mean_and_never_the_minimum()
    {
        // The intent floor is an average over the sentence. A confident 'mortar' plus one bad digit
        // clears it comfortably, which is exactly why the digit minimum is stored separately.
        var utterance = VoskResultReader.Read(
            """
            {"result":[{"conf":1.0,"word":"mortar"},{"conf":0.4,"word":"eight"}],"text":"mortar eight"}
            """);

        Assert.Equal(0.7, utterance.Confidence, 6);
        Assert.Equal(0.4m, utterance.MinDigitConfidence);
    }

    [Fact]
    public void An_empty_result_is_an_empty_utterance()
    {
        Assert.True(VoskResultReader.Read("""{"text":""}""").IsEmpty);
        Assert.True(VoskResultReader.Read("{}").IsEmpty);
        Assert.True(VoskResultReader.Read(string.Empty).IsEmpty);
        Assert.True(VoskResultReader.Read(null).IsEmpty);
    }

    [Fact]
    public void Malformed_json_is_an_empty_utterance_and_never_a_throw()
    {
        Assert.True(VoskResultReader.Read("{\"result\":").IsEmpty);
        Assert.True(VoskResultReader.Read("[1,2,3]").IsEmpty);
    }

    [Fact]
    public void A_hypothesis_with_no_word_scores_is_rejected_rather_than_given_one()
    {
        // This is the alternatives shape. Vosk drops per-word conf in it, so every token scores
        // zero and the utterance falls below the intent floor. Nothing is sent, which is the safe
        // direction, and it is why the engine does not turn alternatives on.
        var utterance = VoskResultReader.Read(
            """
            {"alternatives":[
              {"confidence":361.5,"result":[{"word":"mortar"},{"word":"eight"}],"text":"mortar eight"},
              {"confidence":355.1,"result":[{"word":"mortar"},{"word":"three"}],"text":"mortar three"}]}
            """);

        Assert.Equal("mortar eight", utterance.Text);
        Assert.Equal(0, utterance.Confidence);
        Assert.Equal(0m, utterance.MinDigitConfidence);
        Assert.Single(utterance.Alternatives);
        Assert.Equal("mortar three", utterance.Alternatives[0].Text);
    }

    [Fact]
    public void A_confidence_outside_zero_to_one_is_clamped()
    {
        var utterance = VoskResultReader.Read(
            """{"result":[{"conf":4.2,"word":"mortar"},{"conf":-1,"word":"four"}],"text":"mortar four"}""");

        Assert.Equal(1.0, utterance.Tokens[0].Confidence);
        Assert.Equal(0.0, utterance.Tokens[1].Confidence);
    }

    [Fact]
    public void Text_with_no_word_list_scores_zero_rather_than_borrowing_a_number()
    {
        var utterance = VoskResultReader.Read("""{"text":"mortar urgent"}""");

        Assert.Equal("mortar urgent", utterance.Text);
        Assert.Equal(0, utterance.Confidence);
    }
}
