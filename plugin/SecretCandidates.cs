using System.Collections.Generic;
using System.Linq;

// The secret helper's bookkeeping (NativeSecretHelper finds the facts and draws the list). Pure: no Unity or engine types, so the
// launcher tests run it.
// When the opponent puts a hidden Secret into play, every Secret their class could have is a candidate for it. A candidate is ruled
// out for that Secret when the player did something that would have set it off and the Secret stayed hidden. The list shows the
// candidates of all their hidden Secrets: a card stays possible while it is possible for any of them.
internal sealed class SecretOption
{
    internal string Id;
    internal string Name;
    internal int Cost;
    internal bool Legendary;
}

internal sealed class SecretRow
{
    internal SecretOption Option;
    internal bool Possible;
}

internal sealed class SecretCandidates
{
    private readonly Dictionary<string, SecretOption> options = new Dictionary<string, SecretOption>();
    // Each hidden Secret's candidates that are still possible, by its entity.
    private readonly Dictionary<int, HashSet<string>> possible = new Dictionary<int, HashSet<string>>();
    // Each hidden Secret's whole pool, so a ruled-out card still shows (dimmed).
    private readonly Dictionary<int, List<string>> pools = new Dictionary<int, List<string>>();

    internal int Hidden => possible.Count;
    internal bool Tracks(int secret) => possible.ContainsKey(secret);

    // A hidden Secret the helper has not seen yet starts with the whole pool.
    internal void Track(int secret, IEnumerable<SecretOption> pool)
    {
        if (possible.ContainsKey(secret)) return;
        var ids = new List<string>();
        foreach (var option in pool)
        {
            if (option == null || string.IsNullOrEmpty(option.Id) || ids.Contains(option.Id)) continue;
            options[option.Id] = option;
            ids.Add(option.Id);
        }
        pools[secret] = ids;
        possible[secret] = new HashSet<string>(ids);
    }

    // Forgets the Secrets that are no longer hidden in play: revealed, destroyed or gone.
    internal void Keep(ICollection<int> hidden)
    {
        foreach (int secret in possible.Keys.Where(id => !hidden.Contains(id)).ToList()) { possible.Remove(secret); pools.Remove(secret); }
    }

    internal bool RuleOut(int secret, string card) => possible.TryGetValue(secret, out var set) && set.Remove(card);

    internal IReadOnlyCollection<string> Possible(int secret) =>
        possible.TryGetValue(secret, out var set) ? (IReadOnlyCollection<string>)set : new string[0];

    // Every candidate of any hidden Secret, by cost then name; possible while any hidden Secret could still be it.
    internal List<SecretRow> Rows()
    {
        var rows = new List<SecretRow>();
        foreach (string id in pools.Values.SelectMany(p => p).Distinct())
            rows.Add(new SecretRow { Option = options[id], Possible = possible.Values.Any(set => set.Contains(id)) });
        return rows.OrderBy(r => r.Option.Cost).ThenBy(r => r.Option.Name, System.StringComparer.Ordinal).ToList();
    }

    // One line for the log: each hidden Secret and how many candidates it has left.
    internal string Summary() =>
        string.Join(" ", possible.Keys.OrderBy(id => id).Select(id => id + ":" + possible[id].Count + "/" + pools[id].Count));
}
