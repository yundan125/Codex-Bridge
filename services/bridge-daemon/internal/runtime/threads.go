package runtime

import (
	"context"
	"errors"
	"strings"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/security"
)

func (m *Manager) CreateThread(ctx context.Context, cwd string) (string, error) {
	client, err := m.runningClient()
	if err != nil {
		return "", err
	}
	settings := m.loadCodexDefaults()
	if strings.TrimSpace(cwd) == "" {
		cwd = settings.WorkingDirectory
	}
	sandboxText := settings.PermissionMode
	if strings.TrimSpace(sandboxText) == "" {
		sandboxText = m.Status().SandboxMode
	}
	sandbox, err := security.ParseSandboxMode(sandboxText)
	if err != nil {
		return "", err
	}
	raw, err := client.ThreadStart(ctx, strings.TrimSpace(cwd), strings.TrimSpace(settings.DefaultModel), sandbox)
	if err != nil {
		return "", err
	}
	threadID := firstNonEmpty(textValue(nestedMap(raw, "thread")["id"]), textValue(raw["threadId"]))
	if threadID == "" {
		return "", errors.New("Codex app-server did not return a thread id")
	}
	return threadID, nil
}
