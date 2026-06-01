using System.Reflection;

namespace Sable.Gpu;

/// <summary>Loads WGSL shader sources embedded in this assembly (Shaders/*.wgsl).</summary>
public static class ShaderLibrary
{
    public static string Load(string name)
    {
        var asm = typeof(ShaderLibrary).Assembly;
        // Embedded resource names are dotted: Sable.Gpu.Shaders.<name>.wgsl
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".{name}.wgsl", StringComparison.OrdinalIgnoreCase)
                              || n.EndsWith($".{name}", StringComparison.OrdinalIgnoreCase));
        if (resource is null)
            throw new FileNotFoundException(
                $"WGSL shader '{name}' not found. Available: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
