using Merkle.Core.Processes;
using Merkle.Core.Errors;

namespace Merkle.Build;

public sealed class AdapterBuildCatalog : IBuildAdapterCatalog
{
    private readonly IReadOnlyDictionary<string, IBuildAdapter> _byName;

    public AdapterBuildCatalog(IProcessRunner processRunner)
        : this([
            new DotNetBuildAdapter(processRunner),
            new GoBuildAdapter(processRunner),
            new PythonBuildAdapter(processRunner),
            new JavaBuildAdapter(processRunner)])
    {
    }

    public AdapterBuildCatalog(IEnumerable<IBuildAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        Adapters = adapters.ToArray();
        if (Adapters.Count == 0) throw new ArgumentException("At least one build adapter is required.", nameof(adapters));
        var names = new Dictionary<string, IBuildAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in Adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!names.TryAdd(adapter.Definition.Id, adapter))
                throw new ArgumentException($"Duplicate adapter '{adapter.Definition.Id}'.", nameof(adapters));
            foreach (var alias in adapter.Definition.Aliases)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(alias, adapter.Definition.Id)) continue;
                if (!names.TryAdd(alias, adapter))
                    throw new ArgumentException($"Duplicate adapter alias '{alias}'.", nameof(adapters));
            }
        }
        _byName = names;
    }

    public IReadOnlyList<IBuildAdapter> Adapters { get; }

    public IBuildAdapter Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_byName.TryGetValue(name.Trim(), out var adapter))
            throw new ArgumentException($"Unknown adapter '{name}'.", nameof(name));
        return adapter;
    }

    public IReadOnlyList<IBuildAdapter> ResolveAll() => Adapters;

    public IReadOnlyList<IBuildAdapter> ResolveMany(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Select(Resolve).Distinct().ToArray();
    }

    public IReadOnlyList<IBuildAdapter> ResolveSelection(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            throw new ConfigurationException("InvalidOptionValue", "At least one adapter must be selected.");
        }

        if (names.Contains("all", StringComparer.OrdinalIgnoreCase))
        {
            if (names.Count != 1)
            {
                throw new ConfigurationException("InvalidOptionValue", "Adapter 'all' cannot be combined with named adapters.");
            }

            return ResolveAll();
        }

        var resolved = new List<IBuildAdapter>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !_byName.TryGetValue(name.Trim(), out var adapter))
            {
                throw new ConfigurationException("UnknownAdapter", $"Unknown adapter '{name}'.");
            }

            if (!resolved.Contains(adapter)) resolved.Add(adapter);
        }

        return resolved;
    }
}
