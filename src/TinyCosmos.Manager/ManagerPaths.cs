namespace TinyCosmos.Manager;

public static class ManagerPaths
{
    public const string StateDirectoryName = ".tiny-cosmos";
    public const string StateDatabaseFileName = "state.sqlite3";

    public static string DefaultStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        StateDirectoryName,
        StateDatabaseFileName);
}
