package config

import (
	"errors"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strings"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/security"
)

type Options struct {
	Listen            string
	Token             string
	CodexPath         string
	SandboxMode       string
	Version           string
	DataDir           string
	LogDir            string
	CodexSettingsFile string
}

type Paths struct {
	DataDir                    string
	LogDir                     string
	LogFile                    string
	BindingsFile               string
	ConversationNumbersFile    string
	ThreadNumbersFile          string
	OpenClawSessionNumbersFile string
	TasksFile                  string
	ProjectsFile               string
	MirrorFile                 string
	CommandsFile               string
	CodexSettingsFile          string
}

func (o Options) Validate() error {
	if strings.TrimSpace(o.Token) == "" {
		return errors.New("--token is required")
	}
	host, _, err := net.SplitHostPort(o.Listen)
	if err != nil {
		return fmt.Errorf("invalid --listen address: %w", err)
	}
	if host != "127.0.0.1" {
		return fmt.Errorf("refusing non-loopback listen host %q; only 127.0.0.1 is allowed", host)
	}
	if _, err := security.ParseSandboxMode(o.SandboxMode); err != nil {
		return fmt.Errorf("invalid --sandbox %q; expected read-only, workspace-write, or danger-full-access", o.SandboxMode)
	}
	return nil
}

func UserPaths(dataOverride, logOverride, codexSettingsOverride string) (Paths, error) {
	dataDir := strings.TrimSpace(dataOverride)
	if dataDir == "" {
		documents := strings.TrimSpace(os.Getenv("USERPROFILE"))
		if documents != "" {
			dataDir = filepath.Join(documents, "Documents", "CloudLight", "CloudLight Codex Bridge")
		} else {
			userConfig, err := os.UserConfigDir()
			if err != nil {
				return Paths{}, fmt.Errorf("resolve user data directory: %w", err)
			}
			dataDir = filepath.Join(userConfig, "CloudLight", "CloudLight Codex Bridge")
		}
	}
	dataDir, err := filepath.Abs(dataDir)
	if err != nil {
		return Paths{}, fmt.Errorf("resolve data directory: %w", err)
	}
	stateDir := filepath.Join(dataDir, "data")
	logDir := strings.TrimSpace(logOverride)
	if logDir == "" {
		logDir = filepath.Join(dataDir, "logs")
	}
	logDir, err = filepath.Abs(logDir)
	if err != nil {
		return Paths{}, fmt.Errorf("resolve log directory: %w", err)
	}
	codexSettingsFile := strings.TrimSpace(codexSettingsOverride)
	if codexSettingsFile == "" {
		codexSettingsFile = filepath.Join(dataDir, "config", "codex-settings.json")
	}
	codexSettingsFile, err = filepath.Abs(codexSettingsFile)
	if err != nil {
		return Paths{}, fmt.Errorf("resolve Codex settings path: %w", err)
	}
	if err := os.MkdirAll(logDir, 0o700); err != nil {
		return Paths{}, fmt.Errorf("create log directory: %w", err)
	}
	if err := os.MkdirAll(stateDir, 0o700); err != nil {
		return Paths{}, fmt.Errorf("create state directory: %w", err)
	}
	return Paths{
		DataDir:                    dataDir,
		LogDir:                     logDir,
		LogFile:                    filepath.Join(logDir, "bridge-daemon.log"),
		BindingsFile:               filepath.Join(dataDir, "bindings.json"),
		ConversationNumbersFile:    filepath.Join(stateDir, "conversation-numbers.json"),
		ThreadNumbersFile:          filepath.Join(stateDir, "thread-numbers.json"),
		OpenClawSessionNumbersFile: filepath.Join(stateDir, "openclaw-session-numbers.json"),
		TasksFile:                  filepath.Join(stateDir, "tasks.json"),
		ProjectsFile:               filepath.Join(stateDir, "projects.json"),
		MirrorFile:                 filepath.Join(stateDir, "mirror-state.json"),
		CommandsFile:               filepath.Join(stateDir, "commands.json"),
		CodexSettingsFile:          codexSettingsFile,
	}, nil
}
