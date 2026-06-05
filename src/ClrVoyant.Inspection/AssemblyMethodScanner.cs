using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ClrVoyant.Core;

namespace ClrVoyant.Inspection;

/// <summary>
/// Lists the methods declared in a managed assembly by reading its metadata
/// statically — no running process, no symbols required. This is the discovery
/// half of the no-source workflow: point it at a deployed DLL, find the method you
/// want, then break there with set_function_breakpoint (which binds from the PDB).
/// </summary>
public static class AssemblyMethodScanner
{
    /// <summary>
    /// Enumerate methods in <paramref name="assemblyPath"/>, optionally filtered by a
    /// type-name and/or method-name substring (case-insensitive). Compiler-generated
    /// members (names containing '&lt;') are skipped — their mangled names are not
    /// usable as function breakpoints. Results are capped at <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<MethodSymbol> ListMethods(
        string assemblyPath, string? typeFilter = null, string? methodFilter = null, int max = 200)
    {
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Assembly not found: {assemblyPath}", assemblyPath);

        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata)
            throw new InvalidOperationException($"Not a managed assembly (no metadata): {assemblyPath}");

        var md = pe.GetMetadataReader();
        var result = new List<MethodSymbol>();

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            string declaringType = FullTypeName(md, type);
            if (declaringType.Length == 0) continue; // <Module> and the like
            if (typeFilter is not null && !declaringType.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                string name = md.GetString(method.Name);
                if (name.Contains('<')) continue; // compiler-generated / local function
                if (methodFilter is not null && !name.Contains(methodFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new MethodSymbol($"{declaringType}.{name}", declaringType, name));
                if (result.Count >= max) return result;
            }
        }
        return result;
    }

    // Build the metadata type name, prefixing the namespace and joining nested types
    // with '+', matching how netcoredbg expects a function-breakpoint target.
    static string FullTypeName(MetadataReader md, TypeDefinition type)
    {
        string name = md.GetString(type.Name);
        if (type.IsNested)
        {
            var outer = md.GetTypeDefinition(type.GetDeclaringType());
            return $"{FullTypeName(md, outer)}+{name}";
        }
        string ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }
}
