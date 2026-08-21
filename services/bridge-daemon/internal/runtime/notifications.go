package runtime

import (
	"fmt"
	"strings"
	"time"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/appserver"
	"cloudlight.dev/codexbridge/bridge-daemon/internal/control"
	"cloudlight.dev/codexbridge/bridge-daemon/internal/events"
	"cloudlight.dev/codexbridge/bridge-daemon/internal/interactions"
	bridgelog "cloudlight.dev/codexbridge/bridge-daemon/internal/logging"
)

func (m *Manager) handleAppServerEvent(event appserver.Event) {
	if event.Channel == "transport_error" {
		message := bridgelog.Redact(textValue(event.Params["message"]))
		m.mu.Lock()
		m.status.LastError = message
		m.mu.Unlock()
		m.broker.Publish(events.Error, map[string]any{"code": "appserver_transport", "message": message})
		return
	}
	if event.Channel == "stderr" {
		m.handleAppServerStderr(event.Params)
		return
	}
	if event.Channel == "server_request" {
		m.handleServerRequest(event)
		return
	}
	if event.Channel != "notification" {
		return
	}
	m.handleNotification(event.Method, event.Params)
}

func (m *Manager) handleServerRequest(event appserver.Event) {
	item := m.interactions.Add(event.Method, event.ID, event.Params, time.Now())
	state := m.RuntimeState(item.ThreadID)
	if item.Kind == interactions.KindUserInput {
		state.State = StateWaitingUserInput
	} else {
		state.State = StateWaitingApproval
	}
	if state.Origin == "" {
		state.Origin = "external"
	}
	if state.TurnID == "" {
		state.TurnID = item.TurnID
	}
	if state.StartedAt == "" {
		state.StartedAt = item.CreatedAt
	}
	m.setState(state, true)
	m.broker.PublishScoped(events.InteractionRequested, item.ThreadID, item.TurnID, item.ItemID, map[string]any{"interaction": item})
	if item.Kind == interactions.KindUnknown {
		m.logger.Printf("unknown Codex server request requires local denial: method=%s", safeMethod(event.Method))
	}
}

func (m *Manager) handleNotification(method string, params map[string]any) {
	lower := strings.ToLower(method)
	threadID, turnID, itemID := eventIDs(params)
	if strings.Contains(lower, "warning") || strings.Contains(lower, "systemerror") {
		message := safeErrorMessage(params)
		m.recordAppServerIssue(message, strings.Contains(lower, "systemerror") || persistenceFailureText(message))
	}

	if strings.Contains(lower, "reasoning") {
		// Raw or summarized reasoning notifications are deliberately excluded from
		// the public event stream. The UI only receives normal status and answer text.
		return
	}
	if strings.Contains(lower, "serverrequest/resolved") || strings.Contains(lower, "request/resolved") {
		requestID := rpcIDText(params["requestId"])
		if requestID == "" {
			requestID = rpcIDText(params["id"])
		}
		if item, ok := m.interactions.ResolveByRequest(requestID); ok {
			m.afterInteractionChange(item)
			m.broker.PublishScoped(events.InteractionResolved, item.ThreadID, item.TurnID, item.ItemID, map[string]any{"interaction": item})
		}
		return
	}

	switch lower {
	case "turn/started":
		m.onTurnStarted(threadID, turnID)
		return
	case "turn/completed":
		m.onTurnCompleted(threadID, turnID, params)
		return
	case "thread/status/changed":
		m.onThreadStatusChanged(threadID, params)
		return
	case "item/agentmessage/delta":
		trace, valid := m.validateTraceEvent(method, threadID, turnID)
		if !valid {
			return
		}
		if trace != nil {
			trace.recordItem("agentMessage", itemID)
			m.logger.Printf("rpcTrace stage=item/agentMessage/delta selectedThreadId=%s notificationThreadId=%s notificationTurnId=%s itemId=%s", trace.SelectedThreadID, threadID, turnID, itemID)
		}
		delta := textValue(params["delta"])
		if delta != "" {
			m.bufferAssistantDelta(threadID, turnID, itemID, delta)
		}
		return
	case "item/commandexecution/outputdelta":
		if _, valid := m.validateTraceEvent(method, threadID, turnID); !valid {
			return
		}
		delta := textValue(params["delta"])
		if delta == "" {
			delta = textValue(params["outputDelta"])
		}
		if delta != "" {
			m.broker.PublishScoped(events.ToolUpdated, threadID, turnID, itemID, map[string]any{"outputDelta": delta})
		}
		return
	case "turn/diff/updated":
		if _, valid := m.validateTraceEvent(method, threadID, turnID); !valid {
			return
		}
		m.broker.PublishScoped(events.FileChanged, threadID, turnID, itemID, map[string]any{"diff": textValue(params["diff"]), "status": "updated"})
		return
	}

	if strings.HasSuffix(lower, "item/started") || lower == "item/started" {
		m.onItemEvent(true, threadID, turnID, itemID, params)
		return
	}
	if strings.HasSuffix(lower, "item/completed") || lower == "item/completed" {
		m.onItemEvent(false, threadID, turnID, itemID, params)
		return
	}
	if lower == "error" || strings.HasSuffix(lower, "/error") {
		message := safeErrorMessage(params)
		m.broker.PublishScoped(events.Error, threadID, turnID, itemID, map[string]any{"code": "codex_error", "message": message})
		return
	}
	if strings.HasPrefix(lower, "thread/") || strings.HasPrefix(lower, "turn/") || strings.HasPrefix(lower, "item/") {
		m.logger.Printf("unrecognized Codex notification: method=%s", safeMethod(method))
		m.broker.PublishScoped(events.ThreadUpdated, threadID, turnID, itemID, map[string]any{"source": "unknown-notification", "method": safeMethod(method)})
	}
}

func (m *Manager) handleAppServerStderr(params map[string]any) {
	message := bridgelog.Redact(textValue(params["message"]))
	warning, _ := params["warning"].(bool)
	persistenceError, _ := params["persistenceError"].(bool)
	if !warning && !persistenceError {
		return
	}
	m.recordAppServerIssue(message, persistenceError)
	if persistenceError {
		m.broker.Publish(events.Error, map[string]any{"code": "appserver_persistence", "message": "Codex App Server reported a persistence error; the active Turn will not be reported as persisted."})
	}
}

func (m *Manager) recordAppServerIssue(message string, persistenceError bool) {
	m.traceMu.RLock()
	allTraces := make([]*turnTrace, 0, len(m.traces))
	for _, trace := range m.traces {
		allTraces = append(allTraces, trace)
	}
	m.traceMu.RUnlock()
	traces := make([]*turnTrace, 0, len(allTraces))
	for _, trace := range allTraces {
		if !trace.acceptsDiagnostics() {
			continue
		}
		state := m.RuntimeState(trace.SelectedThreadID)
		if isActiveState(state.State) {
			traces = append(traces, trace)
		}
	}
	// App Server stderr has no Thread/Turn identity. A real persistence error can
	// only be attributed when exactly one local trace is active; otherwise keep
	// it as a warning and let rollout plus independent thread/read decide.
	attributedPersistenceError := persistenceError && len(traces) == 1
	for _, trace := range traces {
		trace.addStderr(message, attributedPersistenceError)
		m.logger.Printf("rpcTrace stage=app-server-stderr selectedThreadId=%s warning=true persistenceError=%t unscopedActiveTraces=%d message=%s", trace.SelectedThreadID, attributedPersistenceError, len(traces), message)
	}
}

func persistenceFailureText(message string) bool {
	lower := strings.ToLower(message)
	known := strings.Contains(lower, "failed to record rollout items") || strings.Contains(lower, "failed to queue rollout items")
	area := strings.Contains(lower, "rollout") || strings.Contains(lower, "persist") || strings.Contains(lower, "state database")
	return known || (area && (strings.Contains(lower, "failed") || strings.Contains(lower, "error")))
}

func (m *Manager) onTurnStarted(threadID, turnID string) {
	if threadID == "" {
		return
	}
	trace, valid := m.validateTraceEvent("turn/started", threadID, turnID)
	if !valid {
		return
	}
	m.stateMu.RLock()
	state, exists := m.states[threadID]
	starting := m.starting[threadID]
	m.stateMu.RUnlock()
	if !exists {
		state = control.RuntimeState{ThreadID: threadID}
	}
	local := trace != nil || starting || (controllableOrigin(state.Origin) && (state.TurnID == "" || state.TurnID == turnID))
	state.State = StateRunningExternal
	state.Origin = "external"
	if local {
		state.State = StateRunningLocal
		state.Origin = "local"
	}
	state.TurnID = turnID
	state.StartedAt = firstNonEmpty(state.StartedAt, nowText())
	state.Error = ""
	m.setState(state, true)
	if trace != nil {
		m.logger.Printf("rpcTrace stage=turn/started selectedThreadId=%s notificationThreadId=%s notificationTurnId=%s", trace.SelectedThreadID, threadID, turnID)
	}
	m.broker.PublishScoped(events.TurnStarted, threadID, turnID, "", map[string]any{"status": state.State, "source": state.Origin})
}

func (m *Manager) onTurnCompleted(threadID, turnID string, params map[string]any) {
	trace, valid := m.validateTraceEvent("turn/completed", threadID, turnID)
	if !valid {
		return
	}
	m.flushDeltaForTurn(threadID, turnID)
	m.logger.Printf("latency stage=turn_completed threadId=%s turnId=%s at=%s source=appserver", threadID, turnID, nowText())
	state := m.RuntimeState(threadID)
	if state.ThreadID == "" {
		state.ThreadID = threadID
	}
	if state.TurnID == "" {
		state.TurnID = turnID
	}
	if trace == nil {
		state.State = StateIdle
		state.TurnID = ""
		state.Origin = ""
		state.Error = ""
		m.setState(state, true)
		m.broker.PublishScoped(events.ThreadUpdated, threadID, turnID, "", map[string]any{"runtime": state, "source": "appserver"})
		return
	}
	status := strings.ToLower(statusText(firstNonNil(params["status"], nestedValue(params, "turn", "status"))))
	message := safeErrorMessage(params)
	hasCompletionError := completionErrorPresent(params)
	if trace != nil {
		m.logger.Printf("rpcTrace stage=turn/completed selectedThreadId=%s notificationThreadId=%s notificationTurnId=%s status=%s", trace.SelectedThreadID, threadID, turnID, status)
		trace.markCompleted()
	}
	eventType := events.TurnFailed
	state.State = completedNotificationState(status)
	switch {
	case hasCompletionError:
		state.State = StatePersistenceFailed
		state.Error = "Codex turn/completed included an error; content omitted."
		trace.addStderr(state.Error, true)
		eventType = events.TurnFailed
	case strings.Contains(status, "interrupt"), strings.Contains(status, "cancel"):
		state.Error = "Turn was interrupted before persistence verification."
		eventType = events.TurnInterrupted
	case strings.Contains(status, "fail"), strings.Contains(status, "error"):
		state.Error = message
		eventType = events.TurnFailed
	case status == "completed":
		state.Error = ""
		eventType = events.TurnPersistence
	default:
		state.Error = "turn/completed did not report status=completed"
	}
	state.CanInterrupt = false
	m.setState(state, true)
	for _, item := range m.interactions.ClearTurn(threadID, turnID, "cancelled") {
		m.broker.PublishScoped(events.InteractionResolved, item.ThreadID, item.TurnID, item.ItemID, map[string]any{"interaction": item})
	}
	payload := map[string]any{"status": state.State, "error": state.Error}
	if state.State == StateCompletedUnverified {
		payload["message"] = "Turn completed, persistence verification is running."
	}
	m.broker.PublishScoped(eventType, threadID, turnID, "", payload)
	m.broker.PublishScoped(events.ThreadUpdated, threadID, turnID, "", map[string]any{"runtime": state})
	if state.State == StateCompletedUnverified {
		go m.verifyCompletedTurn(threadID, turnID, trace)
	}
}

func completionErrorPresent(params map[string]any) bool {
	for _, value := range []any{params["error"], nestedValue(params, "turn", "error")} {
		if nonEmptyErrorValue(value) {
			return true
		}
	}
	return false
}

func nonEmptyErrorValue(value any) bool {
	switch typed := value.(type) {
	case nil:
		return false
	case string:
		return strings.TrimSpace(typed) != ""
	case map[string]any:
		for _, nested := range typed {
			if nonEmptyErrorValue(nested) {
				return true
			}
		}
		return false
	case []any:
		for _, nested := range typed {
			if nonEmptyErrorValue(nested) {
				return true
			}
		}
		return false
	default:
		return true
	}
}

func (m *Manager) onThreadStatusChanged(threadID string, params map[string]any) {
	if threadID == "" {
		return
	}
	status := strings.ToLower(statusText(params["status"]))
	state := m.RuntimeState(threadID)
	if state.ThreadID == "" {
		state.ThreadID = threadID
	}
	switch {
	case strings.Contains(status, "waitingonapproval"):
		state.State = StateWaitingApproval
	case strings.Contains(status, "waitingonuserinput"), strings.Contains(status, "waitingoninput"):
		state.State = StateWaitingUserInput
	case strings.HasPrefix(status, "active"), strings.Contains(status, "inprogress"):
		if controllableOrigin(state.Origin) {
			state.State = StateRunningLocal
		} else {
			state.State = StateRunningExternal
			state.Origin = "external"
		}
	case status == "idle" || status == "notloaded":
		if isActiveState(state.State) && state.Origin == "external" {
			state.State = StateIdle
			state.TurnID = ""
			state.Origin = ""
		}
	case strings.Contains(status, "systemerror"):
		m.failTurnTrace(threadID, StateFailed, "Codex Thread entered systemError; content omitted.")
		return
	}
	m.setState(state, true)
}

func (m *Manager) onItemEvent(started bool, threadID, turnID, itemID string, params map[string]any) {
	item := nestedMap(params, "item")
	if item == nil {
		item = params
	}
	typeName := textValue(item["type"])
	if strings.Contains(strings.ToLower(typeName), "reasoning") {
		return
	}
	if itemID == "" {
		itemID = textValue(item["id"])
	}
	trace, valid := m.validateTraceEvent("item/event", threadID, turnID)
	if !valid {
		return
	}
	if trace != nil {
		trace.recordItem(typeName, itemID)
		m.logger.Printf("rpcTrace stage=item/%s selectedThreadId=%s notificationThreadId=%s notificationTurnId=%s itemType=%s itemId=%s started=%t", safeMethod(typeName), trace.SelectedThreadID, threadID, turnID, safeMethod(typeName), itemID, started)
	}
	payload := normalizedItemPayload(item)
	switch typeName {
	case "agentMessage":
		if !started {
			m.flushDelta(threadID, turnID, itemID)
			m.broker.PublishScoped(events.AssistantCompleted, threadID, turnID, itemID, payload)
		}
	case "fileChange":
		if !started {
			m.broker.PublishScoped(events.FileChanged, threadID, turnID, itemID, payload)
		} else {
			payload["status"] = "started"
			m.broker.PublishScoped(events.FileChanged, threadID, turnID, itemID, payload)
		}
	default:
		if isToolType(typeName) {
			eventType := events.ToolStarted
			if !started {
				eventType = events.ToolCompleted
			}
			m.broker.PublishScoped(eventType, threadID, turnID, itemID, payload)
		}
	}
	m.broker.PublishScoped(events.ThreadUpdated, threadID, turnID, itemID, map[string]any{"source": "notification"})
}

func normalizedItemPayload(item map[string]any) map[string]any {
	payload := map[string]any{
		"type":   textValue(item["type"]),
		"status": statusText(item["status"]),
	}
	for _, key := range []string{"command", "cwd", "aggregatedOutput", "output", "path", "kind", "diff", "phase", "name", "tool", "query", "text"} {
		if value := textValue(item[key]); value != "" {
			payload[key] = value
		}
	}
	if changes, ok := item["changes"].([]any); ok {
		clean := make([]map[string]any, 0, len(changes))
		for _, raw := range changes {
			change, _ := raw.(map[string]any)
			if change == nil {
				continue
			}
			clean = append(clean, map[string]any{"path": textValue(change["path"]), "kind": textValue(change["kind"]), "diff": textValue(change["diff"])})
		}
		payload["changes"] = clean
	}
	return payload
}

func isToolType(value string) bool {
	switch value {
	case "commandExecution", "dynamicToolCall", "mcpToolCall", "webSearch", "collabAgentToolCall":
		return true
	default:
		return strings.Contains(strings.ToLower(value), "tool")
	}
}

func (m *Manager) bufferAssistantDelta(threadID, turnID, itemID, delta string) {
	key := strings.Join([]string{threadID, turnID, itemID}, "\x00")
	m.deltaMu.Lock()
	buffer := m.deltas[key]
	if buffer == nil {
		buffer = &deltaBuffer{ThreadID: threadID, TurnID: turnID, ItemID: itemID, FlushAt: time.Now().Add(900 * time.Millisecond)}
		m.deltas[key] = buffer
	}
	if buffer.Text.Len() < 512*1024 {
		buffer.Text.WriteString(delta)
	}
	m.deltaMu.Unlock()
}

func (m *Manager) deltaLoop() {
	ticker := time.NewTicker(200 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-m.ctx.Done():
			return
		case <-ticker.C:
			m.flushReadyDeltas(false)
		}
	}
}

func (m *Manager) flushDeltas() {
	m.flushReadyDeltas(true)
}

func (m *Manager) flushReadyDeltas(force bool) {
	m.deltaMu.Lock()
	buffers := make([]deltaBuffer, 0, len(m.deltas))
	for key, buffer := range m.deltas {
		if buffer.Text.Len() == 0 || (!force && time.Now().Before(buffer.FlushAt)) {
			continue
		}
		buffers = append(buffers, deltaBuffer{ThreadID: buffer.ThreadID, TurnID: buffer.TurnID, ItemID: buffer.ItemID, Text: *new(strings.Builder)})
		buffers[len(buffers)-1].Text.WriteString(buffer.Text.String())
		delete(m.deltas, key)
	}
	m.deltaMu.Unlock()
	for _, buffer := range buffers {
		m.broker.PublishScoped(events.AssistantDelta, buffer.ThreadID, buffer.TurnID, buffer.ItemID, map[string]any{"delta": buffer.Text.String()})
	}
}

func (m *Manager) flushDelta(threadID, turnID, itemID string) {
	key := strings.Join([]string{threadID, turnID, itemID}, "\x00")
	m.deltaMu.Lock()
	buffer := m.deltas[key]
	delete(m.deltas, key)
	m.deltaMu.Unlock()
	if buffer != nil && buffer.Text.Len() > 0 {
		m.broker.PublishScoped(events.AssistantDelta, buffer.ThreadID, buffer.TurnID, buffer.ItemID, map[string]any{"delta": buffer.Text.String()})
	}
}

func (m *Manager) flushDeltaForTurn(threadID, turnID string) {
	m.deltaMu.Lock()
	keys := []string{}
	for key, buffer := range m.deltas {
		if buffer.ThreadID == threadID && (turnID == "" || buffer.TurnID == turnID) {
			keys = append(keys, key)
		}
	}
	buffers := make([]*deltaBuffer, 0, len(keys))
	for _, key := range keys {
		buffers = append(buffers, m.deltas[key])
		delete(m.deltas, key)
	}
	m.deltaMu.Unlock()
	for _, buffer := range buffers {
		if buffer != nil && buffer.Text.Len() > 0 {
			m.broker.PublishScoped(events.AssistantDelta, buffer.ThreadID, buffer.TurnID, buffer.ItemID, map[string]any{"delta": buffer.Text.String()})
		}
	}
}

func eventIDs(params map[string]any) (string, string, string) {
	threadID := textValue(params["threadId"])
	turnID := textValue(params["turnId"])
	itemID := textValue(params["itemId"])
	if threadID == "" {
		threadID = textValue(nestedMap(params, "thread")["id"])
	}
	if turnID == "" {
		turnID = textValue(nestedMap(params, "turn")["id"])
	}
	if itemID == "" {
		itemID = textValue(nestedMap(params, "item")["id"])
	}
	return threadID, turnID, itemID
}

func nestedValue(value map[string]any, object, key string) any {
	nested := nestedMap(value, object)
	if nested == nil {
		return nil
	}
	return nested[key]
}

func firstNonNil(values ...any) any {
	for _, value := range values {
		if value != nil {
			return value
		}
	}
	return nil
}

func statusText(value any) string {
	if text := textValue(value); text != "" {
		return text
	}
	object, _ := value.(map[string]any)
	if object == nil {
		return "unknown"
	}
	result := textValue(object["type"])
	if flags, ok := object["activeFlags"].([]any); ok {
		parts := []string{}
		for _, flag := range flags {
			if text := textValue(flag); text != "" {
				parts = append(parts, text)
			}
		}
		if len(parts) > 0 {
			result += "[" + strings.Join(parts, ",") + "]"
		}
	}
	return firstNonEmpty(result, "unknown")
}

func safeErrorMessage(params map[string]any) string {
	for _, value := range []any{params["message"], params["error"], nestedValue(params, "turn", "error")} {
		if text := textValue(value); text != "" {
			return bridgelog.Redact(text)
		}
		if object, ok := value.(map[string]any); ok {
			if text := textValue(object["message"]); text != "" {
				return bridgelog.Redact(text)
			}
		}
	}
	return "Codex 操作失败"
}

func rpcIDText(value any) string {
	switch typed := value.(type) {
	case string:
		return typed
	case float64:
		return fmt.Sprintf("%.0f", typed)
	default:
		return ""
	}
}

func safeMethod(method string) string {
	method = strings.TrimSpace(method)
	if len(method) > 160 {
		method = method[:160]
	}
	return method
}
