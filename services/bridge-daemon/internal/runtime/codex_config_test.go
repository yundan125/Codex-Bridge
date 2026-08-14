//go:build windows

package runtime

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/events"
	bridgelog "cloudlight.dev/codexbridge/bridge-daemon/internal/logging"
)

func TestApplyCodexPathConnectsAndSerializesReinitialization(t *testing.T) {
	temporaryDirectory := t.TempDir()
	firstCodex := writeFakeCodex(t, temporaryDirectory, "codex-first.cmd", true)
	secondCodex := writeFakeCodex(t, temporaryDirectory, "codex-second.cmd", true)
	invalidCodex := writeFakeCodex(t, temporaryDirectory, "codex-invalid.cmd", false)
	logger, err := bridgelog.New(filepath.Join(temporaryDirectory, "runtime.log"))
	if err != nil {
		t.Fatalf("create logger: %v", err)
	}
	defer logger.Close()

	manager, err := NewManager("test", "http://127.0.0.1:0", filepath.Join(temporaryDirectory, "missing.exe"), "workspace-write", events.NewBroker(), logger, nil)
	if err != nil {
		t.Fatalf("NewManager: %v", err)
	}
	manager.Start()
	defer manager.Close()

	if _, err := manager.ApplyCodexPath(firstCodex, "RunningChatGPT"); err != nil {
		t.Fatalf("apply first Codex path: %v", err)
	}
	firstStatus := waitForConnectedPath(t, manager, firstCodex)
	if firstStatus.CodexCLIPathSource != "RunningChatGPT" || firstStatus.CodexCLIValidationStatus != "succeeded" || firstStatus.CodexCLIConnectionStatus != "connected" {
		t.Fatalf("unexpected first runtime status: %+v", firstStatus)
	}

	if _, err := manager.ApplyCodexPath(invalidCodex, "Manual"); err == nil {
		t.Fatal("invalid Codex path must be rejected")
	}
	unchanged := manager.Status()
	if !unchanged.AppServerRunning || unchanged.AppServerPID != firstStatus.AppServerPID || !samePath(unchanged.CodexCLIPath, firstCodex) {
		t.Fatalf("invalid path disrupted the running app-server: %+v", unchanged)
	}

	if _, err := manager.ApplyCodexPath(secondCodex, "RunningCodex"); err != nil {
		t.Fatalf("apply second Codex path: %v", err)
	}
	secondStatus := waitForConnectedPath(t, manager, secondCodex)
	if secondStatus.CodexCLIPathSource != "RunningCodex" {
		t.Fatalf("second source was not applied: %+v", secondStatus)
	}

	if _, err := manager.ApplyCodexPath(secondCodex, "RunningCodex"); err != nil {
		t.Fatalf("reapply second Codex path: %v", err)
	}
	time.Sleep(300 * time.Millisecond)
	reapplied := manager.Status()
	if !reapplied.AppServerRunning || reapplied.AppServerPID != secondStatus.AppServerPID {
		t.Fatalf("same path started a duplicate app-server: before=%+v after=%+v", secondStatus, reapplied)
	}
	if err := manager.Close(); err != nil {
		t.Fatalf("close manager before restart: %v", err)
	}
	restartedManager, err := NewManager("test", "http://127.0.0.1:0", secondCodex, "workspace-write", events.NewBroker(), logger, nil)
	if err != nil {
		t.Fatalf("NewManager with persisted path: %v", err)
	}
	restartedManager.Start()
	defer restartedManager.Close()
	restartedStatus := waitForConnectedPath(t, restartedManager, secondCodex)
	if restartedStatus.CodexCLIPathSource != "SavedPath" || restartedStatus.CodexCLIConnectionStatus != "connected" {
		t.Fatalf("persisted path did not connect after restart: %+v", restartedStatus)
	}
	logged, err := os.ReadFile(filepath.Join(temporaryDirectory, "runtime.log"))
	if err != nil {
		t.Fatalf("read runtime log: %v", err)
	}
	for _, marker := range []string{"[codex-daemon] applying new Codex path", "[codex-config] runtime path updated", "[codex-app-server] reconnecting with discovered path", "[codex-app-server] connected"} {
		if !strings.Contains(string(logged), marker) {
			t.Fatalf("runtime log is missing %q: %s", marker, logged)
		}
	}
}

func waitForConnectedPath(t *testing.T, manager *Manager, expectedPath string) Status {
	t.Helper()
	deadline := time.Now().Add(8 * time.Second)
	for time.Now().Before(deadline) {
		status := manager.Status()
		if status.AppServerRunning && samePath(status.CodexCLIPath, expectedPath) {
			return status
		}
		time.Sleep(25 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for Codex path %s; last status: %+v", expectedPath, manager.Status())
	return Status{}
}

func writeFakeCodex(t *testing.T, directory, name string, valid bool) string {
	t.Helper()
	path := filepath.Join(directory, name)
	contents := "@echo off\r\n"
	if valid {
		contents += "if \"%~1\"==\"--version\" goto version\r\nif \"%~1\"==\"app-server\" goto server\r\nexit /b 2\r\n:version\r\necho codex-cli test\r\nexit /b 0\r\n:server\r\nset /p initialize=\r\necho {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\r\nset /p initialized=\r\n:hold\r\nping -n 2 127.0.0.1 ^>nul\r\ngoto hold\r\n"
	} else {
		contents += "exit /b 1\r\n"
	}
	if err := os.WriteFile(path, []byte(contents), 0o600); err != nil {
		t.Fatalf("write fake Codex: %v", err)
	}
	return path
}
