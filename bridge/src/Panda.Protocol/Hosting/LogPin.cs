using Microsoft.Extensions.Logging;

namespace Panda.Bridge.Hosting;

/// <summary>
/// Keeps a category subtree at or above a level whatever configuration says (plan-relay-deploy C2 critic #5,
/// plan-public-query-token D1). A configured rule for that category, a longer one, or a provider-specific one would beat
/// a plain <c>AddFilter</c>, so — run from <c>PostConfigure&lt;LoggerFilterOptions&gt;</c>, after configuration is bound —
/// every rule in the subtree is dropped and the level is set for no provider and for each named provider. A wildcard
/// rule (one `*`, matched by prefix and suffix and ranked by its raw length) that could match inside the subtree is
/// dropped too, wherever it would also apply (fail closed; plan-public-query-token diff critic #1).
/// </summary>
internal static class LogPin
{
    public static void AtLeast(LoggerFilterOptions options, string category, LogLevel level)
    {
        ArgumentNullException.ThrowIfNull(options);
        var providers = options.Rules.Select(r => r.ProviderName).Distinct().ToList();
        var kept = options.Rules
            .Where(r => r.CategoryName is null || !MayMatchInside(r.CategoryName, category))
            .ToList();
        options.Rules.Clear();
        foreach (var rule in kept)
        {
            options.Rules.Add(rule);
        }

        foreach (var provider in providers.Append(null).Distinct())
        {
            options.Rules.Add(new LoggerFilterRule(provider, category, level, filter: null));
        }
    }

    private static bool MayMatchInside(string rule, string category)
    {
        var star = rule.IndexOf('*', StringComparison.Ordinal);
        if (star < 0)
        {
            return rule.StartsWith(category, StringComparison.OrdinalIgnoreCase);
        }

        // "<prefix>*<suffix>" matches a name that starts with the prefix: some name in the subtree does when one of the
        // two is a prefix of the other
        var prefix = rule[..star];
        return category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || prefix.StartsWith(category, StringComparison.OrdinalIgnoreCase);
    }
}
