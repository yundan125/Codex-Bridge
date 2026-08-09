//go:build !windows

package appserver

import "os/exec"

func configureCommand(_ *exec.Cmd) {}

func terminateOwnedProcess(cmd *exec.Cmd) error {
	if cmd == nil || cmd.Process == nil {
		return nil
	}
	return cmd.Process.Kill()
}
