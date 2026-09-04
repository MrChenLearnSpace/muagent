using MuAgents.Abstractions;

namespace MuAgents.UnitTests;

internal static class TestPaths
{
    public static string NewFile(string extension)
    {
        var directory = RuntimePaths.ResolveWritePath(Path.Combine("data", "tests"), "test directory");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
    }

    public static string NewDirectoryPath(string prefix) =>
        RuntimePaths.ResolveWritePath(
            Path.Combine("data", "tests", $"{prefix}-{Guid.NewGuid():N}"),
            "test directory");
}
