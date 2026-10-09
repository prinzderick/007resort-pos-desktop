using R007.Pos.Core;
using R007.Pos.Core.Api;
using R007.Pos.Core.Offline;

namespace R007.Pos.ViewModels.Infrastructure;

/// <summary>
/// "Something went wrong" is all the till can say about an error nobody planned for, so the real one is written to a file
/// for IT (<c>%LOCALAPPDATA%\R007Pos\logs\errors.log</c>). Known, explained errors are not logged.
/// </summary>
public static class UnexpectedErrorLog
{
    public static string LogDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R007Pos", "logs");

    public static void Record(Exception ex)
    {
        if (ex is ApiException or ApiUnavailableException or OperatorException or OperationCanceledException or OfflineQueueBlockedException)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, "errors.log"), $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // Writing the log must never break the till.
        }
    }
}
