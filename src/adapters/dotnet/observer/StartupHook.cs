using System.Reflection;

namespace Merkle.Adapters.DotNet.Observer;

/// <summary>Dependency-free startup hook used only through DOTNET_STARTUP_HOOKS.</summary>
public static class StartupHook
{
    private const int MaximumRecords = 4_096;

    public static void Initialize()
    {
        var destination = Environment.GetEnvironmentVariable("MERKLE_OBSERVATION_FILE");
        if (string.IsNullOrWhiteSpace(destination)) return;
        var gate = new object();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Record(Assembly assembly)
        {
            try
            {
                var location = assembly.Location;
                if (string.IsNullOrWhiteSpace(location)) return;
                lock (gate)
                {
                    if (seen.Count >= MaximumRecords || !seen.Add(location)) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.AppendAllText(destination, location + Environment.NewLine);
                }
            }
            catch
            {
                // A startup hook must never alter test execution when observation fails.
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) Record(assembly);
        AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Record(args.LoadedAssembly);
    }
}
