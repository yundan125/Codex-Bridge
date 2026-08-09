//go:build windows

package runtime

import (
	"encoding/json"
	"os/exec"
	"strings"
	"syscall"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/control"
)

func discoverCodexProcesses() []control.CodexProcess {
	const script = `$items = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -ieq 'Codex.exe' -or ($_.Name -ieq 'codex.exe' -and $_.CommandLine -match '(?i)app-server') } | ForEach-Object { $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner -ErrorAction SilentlyContinue; $version = ''; if ($_.ExecutablePath) { try { $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($_.ExecutablePath).FileVersion } catch {} }; [pscustomobject]@{pid=$_.ProcessId;name=$_.Name;username=$(if ($owner.User) { "$($owner.Domain)\$($owner.User)" } else { '' });version=$version;executablePath=$_.ExecutablePath;commandLine=$_.CommandLine} }; ConvertTo-Json -InputObject @($items) -Compress`
	command := exec.Command("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script)
	command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	output, err := command.Output()
	if err != nil || len(output) == 0 {
		return nil
	}
	var raw []struct {
		PID            any    `json:"pid"`
		Name           string `json:"name"`
		Username       string `json:"username"`
		Version        string `json:"version"`
		ExecutablePath string `json:"executablePath"`
		CommandLine    string `json:"commandLine"`
	}
	if err := json.Unmarshal(output, &raw); err != nil {
		return nil
	}
	result := make([]control.CodexProcess, 0, len(raw))
	for _, item := range raw {
		pid := 0
		switch value := item.PID.(type) {
		case float64:
			pid = int(value)
		case string:
			pid = parsePID(value)
		}
		commandLine := safeCodexCommandShape(item.Name, item.CommandLine)
		result = append(result, control.CodexProcess{
			PID: pid, Name: strings.TrimSpace(item.Name), Username: strings.TrimSpace(item.Username), Version: strings.TrimSpace(item.Version),
			ExecutablePath: safeDiagnosticPath(item.ExecutablePath), CommandLine: commandLine,
		})
	}
	return result
}

func safeCodexCommandShape(name, commandLine string) string {
	name = strings.TrimSpace(name)
	if strings.Contains(strings.ToLower(commandLine), "app-server") {
		return name + " app-server [arguments omitted]"
	}
	return name + " [arguments omitted]"
}
