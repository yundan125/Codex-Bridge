using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CloudLight.CodexBridge.Infrastructure;
using CloudLight.CodexBridge.Models;
using CloudLight.CodexBridge.Services;

namespace CloudLight.CodexBridge.ViewModels;

public sealed class LogsViewModel
{
    private readonly LogService _logs;
    private readonly string _logDirectory;

    public LogsViewModel(LogService logs, string logDirectory)
    {
        _logs = logs;
        _logDirectory = logDirectory;
        ClearCommand = new RelayCommand(_ => _logs.Clear());
        OpenLogDirectoryCommand = new RelayCommand(_ => OpenLogDirectory());
    }

    public ObservableCollection<LogEntry> Entries => _logs.Entries;
    public ICommand ClearCommand { get; }
    public ICommand OpenLogDirectoryCommand { get; }

    private void OpenLogDirectory()
    {
        Directory.CreateDirectory(_logDirectory);
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(_logDirectory);
        Process.Start(info);
    }
}

