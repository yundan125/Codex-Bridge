package appserver

import (
	"os"
	"path/filepath"
	"strings"

	bridgelog "cloudlight.dev/codexbridge/bridge-daemon/internal/logging"
)

// StderrDiagnostic is intentionally limited to operational diagnostics. It
// must never be used as a source of user or assistant message content.
type StderrDiagnostic struct {
	Message          string
	Category         string
	Warning          bool
	PersistenceError bool
}

func classifyStderr(line string) StderrDiagnostic {
	// Every line is inspected, but arbitrary stderr is never retained: it may
	// contain unlabeled prompt or response fragments. Only fixed summaries leave
	// this function.
	lower := strings.ToLower(strings.TrimSpace(line))
	warning := strings.Contains(lower, "warning") ||
		strings.Contains(lower, "configwarning") ||
		strings.Contains(lower, "systemerror")

	// A rollout-related word on its own is not a failed persistence operation.
	// Classify it only when the process reports an actual failure/error, plus
	// the two known app-server persistence failures.
	knownFailure := strings.Contains(lower, "failed to record rollout items") ||
		strings.Contains(lower, "failed to queue rollout items")
	persistenceArea := strings.Contains(lower, "rollout") ||
		strings.Contains(lower, "persist") ||
		strings.Contains(lower, "state database")
	failure := strings.Contains(lower, "failed") || strings.Contains(lower, "error")

	category, message := "other", "Codex App Server stderr line inspected; content omitted."
	switch {
	case strings.Contains(lower, "failed to record rollout items"):
		category, message = "rollout-record-failed", "Codex App Server failed to record rollout items."
	case strings.Contains(lower, "failed to queue rollout items"):
		category, message = "rollout-queue-failed", "Codex App Server failed to queue rollout items."
	case strings.Contains(lower, "state database") && failure:
		category, message = "state-database-error", "Codex App Server reported a state database error."
	case strings.Contains(lower, "rollout") && failure:
		category, message = "rollout-error", "Codex App Server reported a rollout persistence error."
	case strings.Contains(lower, "persist") && failure:
		category, message = "persistence-error", "Codex App Server reported a persistence error."
	case strings.Contains(lower, "session") && failure:
		// Session/network transport errors are not persistence evidence. Codex can
		// recover from them (for example by falling back from WebSockets to HTTPS)
		// while still completing and writing the rollout normally.
		category, message = "session-error", "Codex App Server reported a non-persistence session error."
	case strings.Contains(lower, "configwarning"):
		category, message = "config-warning", "Codex App Server reported a configuration warning; content omitted."
	case strings.Contains(lower, "systemerror"):
		category, message = "system-error", "Codex App Server reported a system error; content omitted."
	case warning:
		category, message = "warning", "Codex App Server reported a warning; content omitted."
	}

	return StderrDiagnostic{
		Message:          message,
		Category:         category,
		Warning:          warning,
		PersistenceError: knownFailure || (persistenceArea && failure),
	}
}

// ProbeEnvironment describes only non-secret process-location inputs. The
// probe inherits the daemon environment by leaving exec.Cmd.Env unset.
type ProbeEnvironment struct {
	InheritsParentEnvironment bool
	UserProfile               string
	Home                      string
	CodexHome                 string
	CodexHomeExplicit         bool
	ResolvedCodexDataRoot     string
}

func currentProbeEnvironment() ProbeEnvironment {
	userProfile := os.Getenv("USERPROFILE")
	home := os.Getenv("HOME")
	codexHome, codexHomeExplicit := os.LookupEnv("CODEX_HOME")
	root := strings.TrimSpace(codexHome)
	if root == "" {
		root = strings.TrimSpace(home)
	}
	if root == "" {
		root = strings.TrimSpace(userProfile)
	}
	if root == "" {
		if fallback, err := os.UserHomeDir(); err == nil {
			root = fallback
		}
	}
	if root != "" && strings.TrimSpace(codexHome) == "" {
		root = filepath.Join(root, ".codex")
	}
	return ProbeEnvironment{
		InheritsParentEnvironment: true,
		UserProfile:               userProfile,
		Home:                      home,
		CodexHome:                 codexHome,
		CodexHomeExplicit:         codexHomeExplicit,
		ResolvedCodexDataRoot:     root,
	}
}

// ProbeEnvironmentSnapshot exposes the non-secret environment resolution used
// by both the primary runtime diagnostics and the short-lived probe.
func ProbeEnvironmentSnapshot() ProbeEnvironment {
	return currentProbeEnvironment()
}

func safeProbeError(err error) string {
	if err == nil {
		return ""
	}
	message := bridgelog.Redact(strings.TrimSpace(err.Error()))
	if len(message) > 512 {
		return message[:512] + " [truncated]"
	}
	return message
}
