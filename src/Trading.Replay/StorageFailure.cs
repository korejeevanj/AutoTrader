namespace Trading.Replay;

/// <summary>What counts as local storage being unavailable, so every handler classifies it the same way.</summary>
internal static class StorageFailure
{
    public static bool Is(Exception error) => error is IOException or System.Data.Common.DbException;
}
