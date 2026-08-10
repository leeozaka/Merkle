using Merkle.Core.Domain;
using Merkle.Core.Errors;

namespace Merkle.Core.Adapters;

public sealed class AdapterRegistry
{
    private readonly IReadOnlyDictionary<string, ILanguageAdapter> _adapters;
    private readonly string _supportedProtocol;
    private readonly string _supportedUnitIdentity;
    private readonly string _supportedTestIdentity;

    public AdapterRegistry(
        IEnumerable<ILanguageAdapter> adapters,
        string supportedProtocol = "1.0",
        string supportedUnitIdentity = "1",
        string supportedTestIdentity = "1")
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _supportedProtocol = supportedProtocol;
        _supportedUnitIdentity = supportedUnitIdentity;
        _supportedTestIdentity = supportedTestIdentity;
        var registered = new Dictionary<string, ILanguageAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            var language = adapter.Describe().Language;
            if (!registered.TryAdd(language, adapter))
            {
                throw new ConfigurationException(
                    "DuplicateAdapterRegistration",
                    $"More than one adapter is registered for '{language}'.");
            }
        }

        _adapters = registered;
    }

    public ILanguageAdapter Resolve(
        LanguageSelection selection,
        IReadOnlyCollection<AdapterCapability> requiredCapabilities)
    {
        if (!_adapters.TryGetValue(selection.Language, out var adapter))
        {
            throw new CapabilityException(
                "AdapterUnavailable",
                $"Function not available for: {selection.Language}");
        }

        var descriptor = adapter.Describe();
        if (!StringComparer.Ordinal.Equals(descriptor.ProtocolVersion, _supportedProtocol))
        {
            throw new CapabilityException(
                "UnsupportedProtocol",
                $"Adapter protocol {descriptor.ProtocolVersion} is not compatible with {_supportedProtocol}.");
        }

        if (!StringComparer.Ordinal.Equals(descriptor.UnitIdentityVersion, _supportedUnitIdentity) ||
            !StringComparer.Ordinal.Equals(descriptor.TestIdentityVersion, _supportedTestIdentity))
        {
            throw new CapabilityException(
                "IdentityIncompatible",
                $"Adapter identities for {selection.Language} are not compatible with this index schema.");
        }

        if (!descriptor.Profiles.Contains(selection.Profile) ||
            requiredCapabilities.Any(capability => !descriptor.Capabilities.Contains(capability)))
        {
            throw new CapabilityException(
                "CapabilityUnavailable",
                $"Function not available for: {selection.Language}");
        }

        return adapter;
    }
}
