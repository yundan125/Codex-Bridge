package runtime

import (
	"errors"
	"strings"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/appserver"
)

type ProtocolCompatibilityError struct {
	Message string
	Detail  string
}

func (e *ProtocolCompatibilityError) Error() string { return e.Message }

func turnStartProtocolCompatibilityError(err error) *ProtocolCompatibilityError {
	var rpcError *appserver.RPCError
	if !errors.As(err, &rpcError) || rpcError.Code != -32600 {
		return nil
	}
	message := rpcError.Message
	if !strings.Contains(strings.ToLower(message), "unknown variant") {
		return nil
	}
	if strings.Contains(message, "onRequest") && strings.Contains(message, "on-request") {
		return &ProtocolCompatibilityError{
			Message: "当前 Codex CLI 的 App Server 协议与程序不兼容：approvalPolicy 不支持值 onRequest，当前版本要求 on-request。",
			Detail:  rpcError.Detail,
		}
	}
	return &ProtocolCompatibilityError{
		Message: "当前 Codex CLI 的 App Server 协议与程序不兼容：turn/start 包含当前版本不支持的枚举值。请查看后端日志中的脱敏详情。",
		Detail:  rpcError.Detail,
	}
}
