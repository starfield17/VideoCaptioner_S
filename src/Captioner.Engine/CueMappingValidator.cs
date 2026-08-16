using Captioner.Core;

namespace Captioner.Engine;

public sealed record CueMappingValidation(bool IsValid, bool IdsValid, string Feedback);

public static class CueMappingValidator
{
    public static CueMappingValidation Validate(
        IReadOnlyDictionary<string, string> original,
        IReadOnlyDictionary<string, string> proposed,
        bool checkSimilarity)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(proposed);

        var expected = original.Keys.ToHashSet(StringComparer.Ordinal);
        var actual = proposed.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var empty = proposed
            .Where(pair => expected.Contains(pair.Key) && string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0 || extra.Length > 0 || empty.Length > 0)
        {
            var parts = new List<string>();
            if (missing.Length > 0)
            {
                parts.Add("missing: " + string.Join(", ", missing));
            }

            if (extra.Length > 0)
            {
                parts.Add("extra: " + string.Join(", ", extra));
            }

            if (empty.Length > 0)
            {
                parts.Add("empty: " + string.Join(", ", empty));
            }

            return new(false, false, string.Join("; ", parts) + $". Required keys: {string.Join(", ", expected.Order(StringComparer.Ordinal))}.");
        }

        if (!checkSimilarity)
        {
            return new(true, true, string.Empty);
        }

        var excessive = new List<string>();
        foreach (var id in expected.Order(StringComparer.Ordinal))
        {
            var source = original[id];
            var value = proposed[id];
            var threshold = TextSimilarity.CorrectionThreshold(source);
            var similarity = TextSimilarity.Ratio(source, value);
            if (similarity < threshold)
            {
                excessive.Add($"{id} similarity {similarity:0.0%} < {threshold:0%}");
            }
        }

        if (excessive.Count > 0)
        {
            return new(
                false,
                true,
                "too_changed: " + string.Join("; ", excessive) +
                ". Keep high similarity and make only minimal recognition fixes.");
        }

        return new(true, true, string.Empty);
    }
}
