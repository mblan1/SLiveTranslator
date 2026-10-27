using System;
using System.IO;

namespace SLiveTranslator.Models;

public static class Log
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppSettings.Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
