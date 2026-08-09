package security

import (
	"fmt"
	"strings"
)

type ApprovalPolicy string

const ApprovalOnRequest ApprovalPolicy = "on-request"

type SandboxMode string

const (
	SandboxReadOnly       SandboxMode = "read-only"
	SandboxWorkspaceWrite SandboxMode = "workspace-write"
)

func ParseSandboxMode(value string) (SandboxMode, error) {
	mode := SandboxMode(strings.TrimSpace(value))
	switch mode {
	case SandboxReadOnly, SandboxWorkspaceWrite:
		return mode, nil
	default:
		return "", fmt.Errorf("unsupported sandbox mode: %s", value)
	}
}
