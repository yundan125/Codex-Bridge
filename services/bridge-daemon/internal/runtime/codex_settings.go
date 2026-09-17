package runtime

import (
	"encoding/json"
	"os"
	"strings"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/control"
)

type codexDefaults struct {
	DefaultModel     string `json:"defaultModel"`
	ReasoningEffort  string `json:"reasoningEffort"`
	PermissionMode   string `json:"permissionMode"`
	NetworkAccess    string `json:"networkAccess"`
	WorkingDirectory string `json:"workingDirectory"`
	SessionStrategy  string `json:"sessionStrategy"`
}

func (m *Manager) SetCodexSettingsFile(path string) {
	m.mu.Lock()
	m.codexSettingsFile = strings.TrimSpace(path)
	m.mu.Unlock()
}

func (m *Manager) loadCodexDefaults() codexDefaults {
	m.mu.RLock()
	path := m.codexSettingsFile
	m.mu.RUnlock()
	if path == "" {
		return codexDefaults{}
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return codexDefaults{}
	}
	var settings codexDefaults
	if err := json.Unmarshal(data, &settings); err != nil {
		if m.logger != nil {
			m.logger.Printf("[codex-settings] read failed: %v", err)
		}
		return codexDefaults{}
	}
	return settings
}

func (m *Manager) applyCodexDefaults(request control.StartTurnRequest) control.StartTurnRequest {
	settings := m.loadCodexDefaults()
	if request.Model == nil && strings.TrimSpace(settings.DefaultModel) != "" {
		value := strings.TrimSpace(settings.DefaultModel)
		request.Model = &value
	}
	if request.ReasoningEffort == nil && strings.TrimSpace(settings.ReasoningEffort) != "" {
		value := strings.TrimSpace(settings.ReasoningEffort)
		request.ReasoningEffort = &value
	}
	if request.PermissionMode == nil && strings.TrimSpace(settings.PermissionMode) != "" {
		value := strings.TrimSpace(settings.PermissionMode)
		request.PermissionMode = &value
	}
	if request.NetworkAccess == nil && strings.TrimSpace(settings.NetworkAccess) != "" && !strings.EqualFold(settings.NetworkAccess, "default") {
		value := strings.TrimSpace(settings.NetworkAccess)
		request.NetworkAccess = &value
	}
	if strings.TrimSpace(request.CWD) == "" && strings.TrimSpace(settings.WorkingDirectory) != "" {
		request.CWD = strings.TrimSpace(settings.WorkingDirectory)
	}
	return request
}

func (m *Manager) CodexSessionStrategy() string {
	value := strings.ToLower(strings.TrimSpace(m.loadCodexDefaults().SessionStrategy))
	if value == "new" || value == "latest" {
		return value
	}
	return "project"
}
