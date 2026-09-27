using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Nightrunner.Core.Logging;

namespace Nightrunner.UI.Views;

/// <summary>One log line, ready to bind.</summary>
public sealed class LogRow(LogEntry entry)
{
    public LogEntry Entry => entry;
    public string TimeText => entry.TimeText;
    public string Source => entry.Source;
    public string Message => entry.Message;
    public string DurationText => entry.DurationText;

    public Brush LevelBrush => Skin.Brush(entry.Level switch
    {
        LogLevel.Error => "Error",
        LogLevel.Warn => "Warn",
        _ => "Fg",
    });

    public string Line => $"{entry.Time:yyyy-MM-dd HH:mm:ss}  {entry.Level,-5} {entry.Source,-8} {entry.Message}" +
                          (entry.Duration is null ? "" : $"  ({entry.DurationText})");
}

/// <summary>The log window: everything the app does, as it happens.</summary>
public partial class LogView : UserControl
{
    private readonly ObservableCollection<LogRow> _rows = [];
    private string _filter = "";

    public LogView()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        foreach (var e in Log.Recent) Add(e);
        Log.Written += OnWritten;
        UpdateStatus();
    }

    /// <summary>Stop following the log — called when this window is closed for good.</summary>
    public void Detach() => Log.Written -= OnWritten;

    /// <summary>Raised on whichever thread wrote the entry.</summary>
    private void OnWritten(LogEntry entry) =>
        Dispatcher.BeginInvoke(() =>
        {
            Add(entry);
            UpdateStatus();
            if (BtnFollow.IsChecked == true && _rows.Count > 0) Rows.ScrollIntoView(_rows[^1]);
        });

    private void Add(LogEntry entry)
    {
        if (_filter.Length > 0 &&
            !entry.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase) &&
            !entry.Source.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            return;
        _rows.Add(new LogRow(entry));
        while (_rows.Count > Log.Capacity) _rows.RemoveAt(0);
    }

    private void UpdateStatus() =>
        Status.Text = $"{_rows.Count:N0} lines" + (_filter.Length > 0 ? $" matching '{_filter}'" : "");

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        _filter = Filter.Text.Trim();
        _rows.Clear();
        foreach (var entry in Log.Recent) Add(entry);
        UpdateStatus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Log.Clear();
        _rows.Clear();
        UpdateStatus();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var rows = Rows.SelectedItems.Count > 0 ? Rows.SelectedItems.Cast<LogRow>() : _rows;
        var text = string.Join(Environment.NewLine, rows.Select(r => r.Line));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save the log",
            Filter = "Text (*.log)|*.log|All files (*.*)|*.*",
            FileName = $"nightrunner-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var sb = new StringBuilder();
            foreach (var r in _rows) sb.AppendLine(r.Line);
            File.WriteAllText(dlg.FileName, sb.ToString());
            Log.Info("log", $"saved {_rows.Count:N0} lines to {dlg.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("log", $"save failed: {ex.Message}");
        }
    }
}
