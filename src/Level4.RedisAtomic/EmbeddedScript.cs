using System.Reflection;

namespace Level4.RedisAtomic;

/// <summary>Loads a Lua script shipped as an embedded resource in this assembly.</summary>
internal static class EmbeddedScript
{
    public static string Load(string fileName)
    {
        var asm = typeof(EmbeddedScript).Assembly;
        // Resource names are "<RootNamespace>.Scripts.<fileName>" — match by suffix to stay robust.
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded script '{fileName}' not found.");

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
