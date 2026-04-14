#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace ICSharpCode.CodeConverter.Tests.Vsix;

/// <summary>
/// When the Vsix loads into Visual Studio, every assembly it references must be resolvable
/// by the running devenv.exe. For a subset of BCL polyfill assemblies (Microsoft.Bcl.AsyncInterfaces
/// and friends) VS ships a specific assembly version and defines a binding redirect in devenv.exe.config
/// covering <c>0.0.0.0-&lt;shippedVersion&gt;</c>. Any reference whose assembly version exceeds that range
/// is *not* redirected, so the CLR probes the extension folder instead. That path succeeds only if we
/// happen to ship exactly the requested version, which in turn leads to two different copies of
/// (say) <c>System.IAsyncDisposable</c> loaded side by side and silent <see cref="InvalidCastException"/>
/// / <see cref="MissingMethodException"/> failures at runtime.
///
/// This test reproduces that class of issue without needing a real Visual Studio by statically
/// walking the Vsix output directory. For each supported VS baseline it asserts that every
/// referenced version of a known VS-owned polyfill can be satisfied by the version VS ships plus
/// the binding redirect it declares. The test fails loudly when the Vsix pulls in a newer BCL
/// polyfill than the oldest supported VS.
/// </summary>
public class VsixAssemblyCompatibilityTests
{
    private readonly ITestOutputHelper _output;

    public VsixAssemblyCompatibilityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The set of BCL polyfill assemblies that VS ships and binding-redirects itself. We must
    /// not reference a newer version than the oldest supported VS ships, otherwise VS's
    /// binding redirect can't cover our reference and we end up with duplicate type identities.
    /// </summary>
    /// <remarks>
    /// Versions taken from the BCL polyfill shipped by the corresponding Microsoft.VisualStudio.Threading
    /// package (which is what the devenv.exe.config binding redirect tracks):
    ///     VS 17.14 (the oldest VS2022 we claim to support): Microsoft.VisualStudio.Threading 17.14 → Microsoft.Bcl.AsyncInterfaces 9.0.0.0
    ///     VS 18.x  (VS2026 preview):                         Microsoft.VisualStudio.Threading 18.x  → Microsoft.Bcl.AsyncInterfaces 10.0.0.0
    /// The oldest supported VS sets the ceiling on what we can reference.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, Version> OldestSupportedVsPolyfillVersions =
        new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.Bcl.AsyncInterfaces"] = new Version(9, 0, 0, 0),
        };

    [Fact]
    public void VsixDoesNotReferenceNewerBclPolyfillsThanOldestSupportedVs()
    {
        var vsixOutput = FindVsixOutputDirectory();
        Assert.True(Directory.Exists(vsixOutput),
            $"Expected Vsix output at '{vsixOutput}'. Build the Vsix project first (msbuild Vsix\\Vsix.csproj).");

        var references = CollectReferencesByAssemblyName(vsixOutput);
        var files = CollectFileVersionsByAssemblyName(vsixOutput);

        var failures = new List<string>();
        foreach (var (polyfillName, maxSupportedVersion) in OldestSupportedVsPolyfillVersions) {
            if (!references.TryGetValue(polyfillName, out var refs) || refs.Count == 0) {
                continue;
            }

            var sorted = refs.OrderBy(r => r.RequestedVersion).ToList();
            _output.WriteLine($"{polyfillName}: oldest supported VS ships {maxSupportedVersion}");
            foreach (var r in sorted) {
                _output.WriteLine($"  {r.ReferrerFileName} -> {polyfillName} {r.RequestedVersion}");
            }

            var maxRequested = sorted.Max(r => r.RequestedVersion);
            if (maxRequested > maxSupportedVersion) {
                var offenders = sorted.Where(r => r.RequestedVersion > maxSupportedVersion)
                    .Select(r => $"{r.ReferrerFileName} -> {r.RequestedVersion}");
                failures.Add(
                    $"{polyfillName}: oldest supported VS ships {maxSupportedVersion}, but {string.Join(", ", offenders)} " +
                    "reference a newer version that VS's devenv.exe.config binding redirect cannot unify. " +
                    "Either downgrade the referencing package so the compile-time reference is <= " +
                    $"{maxSupportedVersion} or add a [ProvideBindingRedirection] attribute to the CodeConverterPackage.");
            }

            if (files.TryGetValue(polyfillName, out var onDisk) && onDisk.Version > maxSupportedVersion) {
                failures.Add(
                    $"{polyfillName}: {Path.GetFileName(onDisk.Path)} in the Vsix output is {onDisk.Version} " +
                    $"but the oldest supported VS only ships {maxSupportedVersion}. " +
                    "When both copies load into devenv.exe the CLR will create duplicate type identities.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void VsixReferencesToSameAssemblyAgreeOrAreCoveredByShippedFile()
    {
        var vsixOutput = FindVsixOutputDirectory();
        if (!Directory.Exists(vsixOutput)) {
            return;
        }

        var references = CollectReferencesByAssemblyName(vsixOutput);
        var files = CollectFileVersionsByAssemblyName(vsixOutput);

        var splits = references
            .Where(kv => kv.Value.Select(r => r.RequestedVersion).Distinct().Count() > 1)
            .Where(kv => OldestSupportedVsPolyfillVersions.ContainsKey(kv.Key))
            .ToList();

        foreach (var kv in splits) {
            var distinct = kv.Value.Select(r => r.RequestedVersion).Distinct().OrderBy(v => v).ToList();
            var hasFile = files.TryGetValue(kv.Key, out var fileEntry);
            _output.WriteLine($"Split-version reference to {kv.Key}:");
            foreach (var v in distinct) {
                var referrers = kv.Value.Where(r => r.RequestedVersion == v).Select(r => r.ReferrerFileName);
                _output.WriteLine($"  v{v}: {string.Join(", ", referrers)}");
            }
            _output.WriteLine(hasFile
                ? $"  File on disk: {Path.GetFileName(fileEntry.Path)} v{fileEntry.Version}"
                : "  File on disk: (not shipped; relying on VS-installed copy)");
        }
    }

    private static string FindVsixOutputDirectory()
    {
        // Tests/bin/<Config>/<tfm> -> Vsix/bin/<Config>
        var testAssembly = typeof(VsixAssemblyCompatibilityTests).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(testAssembly)!);
        // Walk up looking for the repo root (containing Vsix folder)
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Vsix", "bin"))) {
            dir = dir.Parent;
        }
        if (dir == null) {
            return Path.Combine(AppContext.BaseDirectory, "Vsix", "bin", "Release");
        }
        var vsixBin = Path.Combine(dir.FullName, "Vsix", "bin");
        // Prefer the same configuration the tests were built with
        var configDirs = Directory.EnumerateDirectories(vsixBin).ToList();
        // Prefer Release if it exists, otherwise any configuration.
        var release = configDirs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Release", StringComparison.OrdinalIgnoreCase));
        return release ?? configDirs.FirstOrDefault() ?? Path.Combine(vsixBin, "Release");
    }

    private record ReferenceEntry(string ReferrerFileName, Version RequestedVersion);

    private static Dictionary<string, List<ReferenceEntry>> CollectReferencesByAssemblyName(string directory)
    {
        var result = new Dictionary<string, List<ReferenceEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll")) {
            if (!TryOpenMetadata(dll, out var md)) {
                continue;
            }
            using (md) {
                var reader = md!.GetMetadataReader();
                if (!reader.IsAssembly) continue;
                foreach (var handle in reader.AssemblyReferences) {
                    var reference = reader.GetAssemblyReference(handle);
                    var name = reader.GetString(reference.Name);
                    if (!result.TryGetValue(name, out var list)) {
                        list = new List<ReferenceEntry>();
                        result[name] = list;
                    }
                    list.Add(new ReferenceEntry(Path.GetFileName(dll), reference.Version));
                }
            }
        }
        return result;
    }

    private static Dictionary<string, (Version Version, string Path)> CollectFileVersionsByAssemblyName(string directory)
    {
        var result = new Dictionary<string, (Version, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll")) {
            if (!TryOpenMetadata(dll, out var md)) {
                continue;
            }
            using (md) {
                var reader = md!.GetMetadataReader();
                if (!reader.IsAssembly) continue;
                var def = reader.GetAssemblyDefinition();
                result[reader.GetString(def.Name)] = (def.Version, dll);
            }
        }
        return result;
    }

    private static bool TryOpenMetadata(string path, out PEReader? peReader)
    {
        peReader = null;
        try {
            var stream = File.OpenRead(path);
            peReader = new PEReader(stream);
            if (!peReader.HasMetadata) {
                peReader.Dispose();
                peReader = null;
                return false;
            }
            return true;
        } catch {
            peReader?.Dispose();
            peReader = null;
            return false;
        }
    }
}
