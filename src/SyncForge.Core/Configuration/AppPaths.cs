namespace SyncForge.Core.Configuration;

public static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SyncForge");

    public static string DatabasePath => Path.Combine(DataDirectory, "syncforge.db");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
