//go:build windows

package appserver

import (
	"os/exec"
	"strconv"
	"syscall"
)

func configureCommand(cmd *exec.Cmd) {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.HideWindow = true
}

func configureBatchCommand(cmd *exec.Cmd, commandLine string) {
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CmdLine: commandLine}
}

func terminateOwnedProcess(cmd *exec.Cmd) error {
	if cmd == nil || cmd.Process == nil {
		return nil
	}
	kill := exec.Command("taskkill.exe", "/PID", strconv.Itoa(cmd.Process.Pid), "/T", "/F")
	kill.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	if err := kill.Run(); err == nil {
		return nil
	}
	return cmd.Process.Kill()
}
