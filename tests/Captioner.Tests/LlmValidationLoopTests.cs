using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Tests;

public sealed class LlmValidationLoopTests
{
    [Fact]
    public void Validator_rejects_missing_and_empty_ids()
    {
        var original = new Dictionary<string, string>(StringComparer.Ordinal) { ["c1"] = "hello", ["c2"] = "world" };
        var proposed = new Dictionary<string, string>(StringComparer.Ordinal) { ["c1"] = "hello", ["c3"] = "nope" };

        var result = CueMappingValidator.Validate(original, proposed, checkSimilarity: false);

        Assert.False(result.IsValid);
        Assert.False(result.IdsValid);
        Assert.Contains("missing", result.Feedback, StringComparison.Ordinal);
        Assert.Contains("extra", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_accepts_id_valid_low_similarity_as_ids_only()
    {
        var original = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["c1"] = "The committee approved the quarterly budget report today after a lengthy debate."
        };
        var proposed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["c1"] = "zzzz zzzz zzzz zzzz zzzz zzzz zzzz zzzz zzzz zzzz zzzz zzzz"
        };

        var result = CueMappingValidator.Validate(original, proposed, checkSimilarity: true);

        Assert.False(result.IsValid);
        Assert.True(result.IdsValid);
        Assert.Contains("too_changed", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void TextSimilarity_uses_a_looser_threshold_for_short_cues()
    {
        Assert.Equal(0.3, TextSimilarity.CorrectionThreshold("hi"));
        Assert.Equal(0.7, TextSimilarity.CorrectionThreshold("one two three four five six seven eight nine ten eleven"));
        Assert.True(TextSimilarity.Ratio("hello world", "hello world") > 0.99);
        Assert.True(TextSimilarity.IsMainlyCjk("你好世界"));
        Assert.False(TextSimilarity.IsMainlyCjk("hello world"));
    }
}
