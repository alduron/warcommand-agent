using WarCommand.Agent.Client.Http;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// A refusal names itself on the strip. Reported: a submit refused with 429 open_request_limit and
/// again with 422 point_count_mismatch, and both read SUBMIT FAILED.
/// </summary>
public class FaultWordsTests
{
    private static WarCommandApiException Api(string code, int status) =>
        new(new ApiError { Code = code, Status = status });

    [Theory]
    [InlineData(ErrorCodes.OpenRequestLimit, 429, "TOO MANY OPEN, CLOSE ONE")]
    [InlineData(ErrorCodes.PointCountMismatch, 422, "WRONG POINT COUNT, TRY AGAIN")]
    [InlineData(ErrorCodes.DeploymentClosed, 409, "DEPLOYMENT CLOSED")]
    [InlineData(ErrorCodes.RateLimited, 429, "SLOW DOWN")]
    [InlineData(ErrorCodes.ValidationError, 422, "SUBMIT REFUSED")]
    public void A_refusal_says_which_one(string code, int status, string word)
    {
        Assert.Equal(word, FaultWords.Submit(Api(code, status)));
    }

    [Fact]
    public void An_unmapped_code_still_says_something()
    {
        Assert.Equal("SUBMIT FAILED", FaultWords.Submit(Api("something_new", 400)));
        Assert.Equal("SUBMIT FAILED", FaultWords.Submit(new HttpRequestException("down")));
    }

    /// <summary>Fragments, not sentences: Convention_WarCommandAgentCopyIsFragmentsNotSentences.</summary>
    [Fact]
    public void Every_word_is_a_fragment()
    {
        foreach (var code in new[]
        {
            ErrorCodes.OpenRequestLimit,
            ErrorCodes.PointCountMismatch,
            ErrorCodes.DeploymentMismatch,
            ErrorCodes.DeploymentClosed,
            ErrorCodes.GroupFrozen,
            ErrorCodes.RoleNotEnabled,
            ErrorCodes.VisitorNotPermitted,
            ErrorCodes.RateLimited,
            ErrorCodes.Unauthenticated,
            ErrorCodes.Forbidden,
            ErrorCodes.UnknownRequestType,
        })
        {
            var word = FaultWords.Submit(Api(code, 422));

            Assert.Equal(word.ToUpperInvariant(), word);
            Assert.DoesNotContain('.', word);
            Assert.True(word.Length <= 28, $"{code} is too long for the strip: {word}");
        }
    }
}
