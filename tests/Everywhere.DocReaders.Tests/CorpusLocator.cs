namespace Everywhere.DocReaders.Tests;

public static class CorpusLocator
{
    public static string CorpusDir
    {
        get
        {
            // Tests copy doc-corpus/* into bin/.../doc-corpus/ via the csproj <None Include>.
            var bin = Path.Combine(AppContext.BaseDirectory, "doc-corpus");
            if (Directory.Exists(bin)) return bin;
            // Fallback for local "dotnet test" runs from repo root.
            var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "doc-corpus"));
            return src;
        }
    }
}
