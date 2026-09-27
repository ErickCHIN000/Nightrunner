using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Nightrunner.Core.Logging;

namespace Nightrunner.UI;

/// <summary>Small things every view needs: Explorer, folder pickers, a text prompt.</summary>
public static class Shell
{
    /// <summary>Open a folder in Explorer, or show a file selected in its folder.</summary>
    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = true });
            Log.Info("shell", $"opened {path} in Explorer");
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            Log.Error("shell", $"could not open {path}: {e.Message}");
        }
    }

    /// <summary>Ask for a folder; null when the user cancels.</summary>
    public static string? PickFolder(DependencyObject? owner, string title, string? startAt = null)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt)) dlg.InitialDirectory = startAt;
        return dlg.ShowDialog(Window.GetWindow(owner as DependencyObject ?? new Window())) == true
            ? dlg.FolderName
            : null;
    }
}
