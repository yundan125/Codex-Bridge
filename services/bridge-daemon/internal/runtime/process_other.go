//go:build !windows

package runtime

import "cloudlight.dev/codexbridge/bridge-daemon/internal/control"

func discoverCodexProcesses() []control.CodexProcess { return nil }
