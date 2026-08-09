package qq

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/channels"
)

const (
	GroupTriggerMentionOnly     = "mention-only"
	GroupTriggerMentionOrPrefix = "mention-or-prefix"
	defaultCommandPrefix        = "/codex"
	defaultMessageRunes         = 1500
)

var ErrEditUnsupported = errors.New("QQ OneBot does not support editing messages")

type ConfigureRequest struct {
	WebSocketURL          string   `json:"webSocketUrl"`
	Token                 *string  `json:"token,omitempty"`
	Enabled               bool     `json:"enabled"`
	AutoStart             bool     `json:"autoStart"`
	ReconnectEnabled      bool     `json:"reconnectEnabled"`
	AllowedPrivateUserIDs []string `json:"allowedPrivateUserIds"`
	AllowedGroupIDs       []string `json:"allowedGroupIds"`
	AllowedGroupUserIDs   []string `json:"allowedGroupUserIds"`
	GroupTriggerMode      string   `json:"groupTriggerMode"`
	CommandPrefix         string   `json:"commandPrefix"`
	SendProgressUpdates   bool     `json:"sendProgressUpdates"`
}

type TestRequest struct {
	WebSocketURL string  `json:"webSocketUrl,omitempty"`
	Token        *string `json:"token,omitempty"`
}

type TestResult struct {
	OK                    bool   `json:"ok"`
	Category              string `json:"category"`
	Message               string `json:"message"`
	SelfID                string `json:"selfId,omitempty"`
	Nickname              string `json:"nickname,omitempty"`
	OneBotVersion         string `json:"oneBotVersion,omitempty"`
	Implementation        string `json:"implementation,omitempty"`
	ImplementationVersion string `json:"implementationVersion,omitempty"`
}

type AdapterEvent struct {
	Kind             string `json:"kind"`
	Code             string `json:"code,omitempty"`
	Reason           string `json:"reason,omitempty"`
	ConversationType string `json:"conversationType,omitempty"`
	ChatID           string `json:"chatId,omitempty"`
	UserID           string `json:"userId,omitempty"`
	MessageID        string `json:"messageId,omitempty"`
	Unsupported      bool   `json:"unsupported,omitempty"`
}

type AdapterStatus struct {
	Type                    string `json:"type"`
	ChannelType             string `json:"channelType"`
	Configured              bool   `json:"configured"`
	Running                 bool   `json:"running"`
	Connected               bool   `json:"connected"`
	State                   string `json:"state"`
	ConnectionState         string `json:"connectionState"`
	WebSocketURL            string `json:"webSocketUrl"`
	SelfID                  string `json:"selfId"`
	Nickname                string `json:"nickname"`
	OneBotVersion           string `json:"oneBotVersion"`
	Implementation          string `json:"implementation"`
	ImplementationVersion   string `json:"implementationVersion"`
	LastConnectedAt         string `json:"lastConnectedAt,omitempty"`
	LastHeartbeatAt         string `json:"lastHeartbeatAt,omitempty"`
	LastEventAt             string `json:"lastEventAt,omitempty"`
	ReconnectCount          int    `json:"reconnectCount"`
	LastErrorCode           string `json:"lastErrorCode,omitempty"`
	LastErrorMessage        string `json:"lastErrorMessage,omitempty"`
	AllowedPrivateUserCount int    `json:"allowedPrivateUserCount"`
	AllowedGroupCount       int    `json:"allowedGroupCount"`
	AllowedGroupUserCount   int    `json:"allowedGroupUserCount"`
	BindingCount            int    `json:"bindingCount"`
	TokenSet                bool   `json:"tokenSet"`
	TokenFingerprint        string `json:"tokenFingerprint,omitempty"`
	AutoStart               bool   `json:"autoStart"`
	ReconnectEnabled        bool   `json:"reconnectEnabled"`
	SendProgressUpdates     bool   `json:"sendProgressUpdates"`
	Enabled                 bool   `json:"enabled"`
	GroupTriggerMode        string `json:"groupTriggerMode"`
	CommandPrefix           string `json:"commandPrefix"`
	HeartbeatIntervalMS     int64  `json:"heartbeatIntervalMs"`
	HeartbeatOnline         bool   `json:"heartbeatOnline"`
	HeartbeatGood           bool   `json:"heartbeatGood"`
}

type MessageHandler func(context.Context, channels.InboundMessage)

type Adapter struct {
	mu          sync.RWMutex
	config      ConfigureRequest
	token       string
	status      AdapterStatus
	client      *Client
	handler     MessageHandler
	events      func(AdapterEvent)
	cancel      context.CancelFunc
	done        chan struct{}
	runID       uint64
	seen        *dedupSet
	clientFn    func(string, string, func([]byte)) (*Client, error)
	backoffBase time.Duration
	backoffMax  time.Duration
}

func NewAdapter(handler MessageHandler) *Adapter {
	config := ConfigureRequest{
		WebSocketURL: DefaultWebSocketURL, ReconnectEnabled: true,
		GroupTriggerMode: GroupTriggerMentionOnly, CommandPrefix: defaultCommandPrefix,
		AllowedPrivateUserIDs: []string{}, AllowedGroupIDs: []string{}, AllowedGroupUserIDs: []string{},
	}
	return &Adapter{
		config: config, handler: handler, seen: newDedupSet(4096, 10*time.Minute),
		clientFn: NewClient, backoffBase: time.Second, backoffMax: 30 * time.Second,
		status: AdapterStatus{
			Type: "qq", ChannelType: "qq", State: "stopped", ConnectionState: "stopped",
			WebSocketURL: DefaultWebSocketURL, ReconnectEnabled: true,
			GroupTriggerMode: GroupTriggerMentionOnly, CommandPrefix: defaultCommandPrefix,
		},
	}
}

func (a *Adapter) Type() string { return "qq" }

func (a *Adapter) Configure(request ConfigureRequest) (AdapterStatus, error) {
	normalized, err := normalizeConfigureRequest(request)
	if err != nil {
		return AdapterStatus{}, err
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.status.Running {
		return AdapterStatus{}, errors.New("stop QQ before changing its configuration")
	}
	if normalized.Token != nil {
		token := strings.TrimSpace(*normalized.Token)
		if token == "" {
			return AdapterStatus{}, errors.New("accessToken cannot be empty; use ClearToken")
		}
		a.token = token
	}
	normalized.Token = nil
	a.config = normalized
	a.refreshStatusLocked()
	a.status.LastErrorCode = ""
	a.status.LastErrorMessage = ""
	return a.status, nil
}

func (a *Adapter) ClearToken(ctx context.Context) error {
	if err := a.Stop(ctx); err != nil {
		return err
	}
	a.mu.Lock()
	a.token = ""
	a.refreshStatusLocked()
	a.status.LastErrorCode = ""
	a.status.LastErrorMessage = ""
	a.mu.Unlock()
	return nil
}

func (a *Adapter) Start(parent context.Context) error {
	a.mu.Lock()
	if a.status.Running {
		a.mu.Unlock()
		return nil
	}
	if !a.config.Enabled {
		a.mu.Unlock()
		return errors.New("QQ adapter is disabled")
	}
	if !hasUsableAllowlist(a.config) {
		a.mu.Unlock()
		return errors.New("QQ requires an allowed private user or both an allowed group and allowed group user")
	}
	ctx, cancel := context.WithCancel(parent)
	a.runID++
	runID := a.runID
	a.cancel = cancel
	a.done = make(chan struct{})
	a.client = nil
	a.status.Running = true
	a.status.Connected = false
	a.status.State = "connecting"
	a.status.ConnectionState = "connecting"
	a.status.ReconnectCount = 0
	a.status.LastErrorCode = ""
	a.status.LastErrorMessage = ""
	a.mu.Unlock()
	a.emit(AdapterEvent{Kind: "connecting"})

	client, login, version, err := a.connect(ctx)
	if err != nil {
		wasCanceled := ctx.Err() != nil
		cancel()
		a.finishInitialFailure(runID, err, wasCanceled)
		return safeAdapterError(err, a.secret())
	}
	a.markConnected(runID, client, login, version)
	go a.supervise(ctx, runID, client)
	return nil
}

func (a *Adapter) Stop(ctx context.Context) error {
	a.mu.Lock()
	if !a.status.Running {
		a.mu.Unlock()
		return nil
	}
	a.status.State = "stopping"
	a.status.ConnectionState = "stopping"
	cancel, done, client := a.cancel, a.done, a.client
	a.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	if client != nil {
		_ = client.Close()
	}
	if done != nil {
		select {
		case <-done:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	return nil
}

func (a *Adapter) Status() channels.Status {
	status := a.QQStatus()
	return channels.Status{Type: "qq", Running: status.Running, State: status.ConnectionState, LastError: status.LastErrorMessage}
}

func (a *Adapter) QQStatus() AdapterStatus {
	a.mu.RLock()
	defer a.mu.RUnlock()
	return a.status
}

func (a *Adapter) SetBindingCount(count int) {
	if count < 0 {
		count = 0
	}
	a.mu.Lock()
	a.status.BindingCount = count
	a.mu.Unlock()
}

func (a *Adapter) SetEventHandler(handler func(AdapterEvent)) {
	a.mu.Lock()
	a.events = handler
	a.mu.Unlock()
}

func (a *Adapter) Test(ctx context.Context, request TestRequest) TestResult {
	a.mu.RLock()
	webSocketURL, token := a.config.WebSocketURL, a.token
	a.mu.RUnlock()
	if strings.TrimSpace(request.WebSocketURL) != "" {
		webSocketURL = request.WebSocketURL
	}
	if request.Token != nil {
		token = strings.TrimSpace(*request.Token)
	}
	normalizedURL, err := NormalizeWebSocketURL(webSocketURL)
	if err != nil {
		category := ClassifyError(err)
		return TestResult{Category: category, Message: categoryMessage(category)}
	}
	client, err := a.clientFn(normalizedURL, token, nil)
	if err != nil {
		category := ClassifyError(err)
		return TestResult{Category: category, Message: categoryMessage(category)}
	}
	login, version, err := client.Start(ctx)
	_ = client.Close()
	if err != nil {
		category := ClassifyError(err)
		return TestResult{Category: category, Message: categoryMessage(category)}
	}
	return TestResult{
		OK: true, Category: "ok", Message: "OneBot Forward WebSocket is reachable",
		SelfID: string(login.UserID), Nickname: login.Nickname, OneBotVersion: version.ProtocolVersion,
		Implementation: version.Implementation, ImplementationVersion: version.ImplementationVersion,
	}
}

func (a *Adapter) SendMessage(ctx context.Context, message channels.OutboundMessage) (channels.OutboundResult, error) {
	a.mu.RLock()
	client, connected := a.client, a.status.Connected
	a.mu.RUnlock()
	if client == nil || !connected {
		return channels.OutboundResult{}, errors.New("QQ adapter is not connected")
	}
	conversationType := strings.ToLower(strings.TrimSpace(message.Address.ConversationType))
	if conversationType != "private" && conversationType != "group" {
		return channels.OutboundResult{}, errors.New("QQ conversationType must be private or group")
	}
	parts := SplitMessage(message.Text, defaultMessageRunes)
	if len(parts) == 0 {
		return channels.OutboundResult{}, errEmptyMessage
	}
	var last channels.OutboundResult
	for index, part := range parts {
		var result SendMessageResult
		var err error
		if conversationType == "private" {
			result, err = client.SendPrivateMessage(ctx, message.Address.ChatID, part)
		} else {
			result, err = client.SendGroupMessage(ctx, message.Address.ChatID, part)
		}
		if err != nil {
			a.mu.Lock()
			category := ClassifyError(err)
			a.setErrorLocked(category, err)
			a.mu.Unlock()
			a.emit(AdapterEvent{Kind: "action_failed", Code: category, ConversationType: conversationType, ChatID: message.Address.ChatID})
			return last, fmt.Errorf("send QQ message part %d of %d: %w", index+1, len(parts), err)
		}
		last.MessageID = string(result.MessageID)
	}
	return last, nil
}

func (a *Adapter) EditMessage(context.Context, string, channels.OutboundMessage) (channels.OutboundResult, error) {
	a.emit(AdapterEvent{Kind: "action_failed", Code: "unsupported-edit"})
	return channels.OutboundResult{}, ErrEditUnsupported
}

func (a *Adapter) connect(ctx context.Context) (*Client, LoginInfo, VersionInfo, error) {
	a.mu.RLock()
	webSocketURL, token := a.config.WebSocketURL, a.token
	a.mu.RUnlock()
	client, err := a.clientFn(webSocketURL, token, func(data []byte) { a.handleRawEvent(ctx, data) })
	if err != nil {
		return nil, LoginInfo{}, VersionInfo{}, err
	}
	login, version, err := client.Start(ctx)
	if err != nil {
		_ = client.Close()
		return nil, LoginInfo{}, VersionInfo{}, err
	}
	return client, login, version, nil
}

func (a *Adapter) supervise(ctx context.Context, runID uint64, client *Client) {
	defer func() {
		a.mu.Lock()
		if a.runID == runID {
			a.client = nil
			a.status.Running = false
			a.status.Connected = false
			if ctx.Err() != nil || a.status.ConnectionState == "stopping" {
				a.status.State = "stopped"
				a.status.ConnectionState = "stopped"
			}
			close(a.done)
		}
		a.mu.Unlock()
		a.emit(AdapterEvent{Kind: "stopped"})
	}()
	backoff := a.backoffBase
	healthTicker := time.NewTicker(5 * time.Second)
	defer healthTicker.Stop()
	for {
		var err error
		var ok bool
		select {
		case <-ctx.Done():
			return
		case <-healthTicker.C:
			lastInbound := client.LastInboundAt()
			if !lastInbound.IsZero() && time.Since(lastInbound) > a.idleLimit() {
				client.Abort(ErrHeartbeatTimeout)
			}
			continue
		case err, ok = <-client.Done():
			if !ok {
				err = nil
			}
		}
		if ctx.Err() != nil {
			return
		}
		a.mu.Lock()
		if a.runID != runID {
			a.mu.Unlock()
			return
		}
		a.client = nil
		a.status.Connected = false
		a.status.State = "connection-failed"
		a.status.ConnectionState = "connection-failed"
		if err != nil {
			a.setErrorLocked(ClassifyError(err), err)
		}
		reconnect := a.config.ReconnectEnabled
		a.mu.Unlock()
		a.emit(AdapterEvent{Kind: "disconnected", Code: ClassifyError(err)})
		if !reconnect {
			return
		}
		for {
			a.mu.Lock()
			if a.runID != runID {
				a.mu.Unlock()
				return
			}
			a.status.State = "reconnecting"
			a.status.ConnectionState = "reconnecting"
			a.status.ReconnectCount++
			a.mu.Unlock()
			a.emit(AdapterEvent{Kind: "reconnecting"})
			if !waitContext(ctx, backoff) {
				return
			}
			nextClient, login, version, connectErr := a.connect(ctx)
			if connectErr == nil {
				client = nextClient
				a.markConnected(runID, client, login, version)
				backoff = a.backoffBase
				break
			}
			if errors.Is(connectErr, ErrAuthentication) {
				a.mu.Lock()
				a.status.State = "authentication-failed"
				a.status.ConnectionState = "authentication-failed"
				a.setErrorLocked("authentication_failed", connectErr)
				a.mu.Unlock()
				a.emit(AdapterEvent{Kind: "error", Code: "authentication_failed"})
				return
			}
			a.mu.Lock()
			category := ClassifyError(connectErr)
			a.setErrorLocked(category, connectErr)
			a.mu.Unlock()
			a.emit(AdapterEvent{Kind: "error", Code: category})
			backoff *= 2
			if backoff > a.backoffMax {
				backoff = a.backoffMax
			}
		}
	}
}

func (a *Adapter) idleLimit() time.Duration {
	a.mu.RLock()
	interval := time.Duration(a.status.HeartbeatIntervalMS) * time.Millisecond
	a.mu.RUnlock()
	if interval <= 0 {
		return 90 * time.Second
	}
	limit := interval * 3
	if limit < 30*time.Second {
		return 30 * time.Second
	}
	if limit > 5*time.Minute {
		return 5 * time.Minute
	}
	return limit
}

func (a *Adapter) finishInitialFailure(runID uint64, err error, canceled bool) {
	a.mu.Lock()
	if a.runID != runID {
		a.mu.Unlock()
		return
	}
	a.status.Running = false
	a.status.Connected = false
	if canceled {
		a.status.State = "stopped"
		a.status.ConnectionState = "stopped"
	} else if errors.Is(err, ErrAuthentication) {
		a.status.State = "authentication-failed"
		a.status.ConnectionState = "authentication-failed"
		a.setErrorLocked("authentication_failed", err)
	} else {
		a.status.State = "connection-failed"
		a.status.ConnectionState = "connection-failed"
		a.setErrorLocked(ClassifyError(err), err)
	}
	close(a.done)
	a.mu.Unlock()
	if canceled {
		a.emit(AdapterEvent{Kind: "stopped"})
		return
	}
	code := ClassifyError(err)
	if errors.Is(err, ErrAuthentication) {
		code = "authentication_failed"
	}
	a.emit(AdapterEvent{Kind: "error", Code: code})
	a.emit(AdapterEvent{Kind: "disconnected", Code: code})
}

func (a *Adapter) markConnected(runID uint64, client *Client, login LoginInfo, version VersionInfo) {
	a.mu.Lock()
	if a.runID != runID {
		a.mu.Unlock()
		_ = client.Close()
		return
	}
	a.client = client
	a.status.Connected = true
	a.status.State = "connected"
	a.status.ConnectionState = "connected"
	a.status.SelfID = string(login.UserID)
	a.status.Nickname = login.Nickname
	a.status.OneBotVersion = version.ProtocolVersion
	a.status.Implementation = version.Implementation
	a.status.ImplementationVersion = version.ImplementationVersion
	a.status.LastConnectedAt = nowText()
	a.status.LastErrorCode = ""
	a.status.LastErrorMessage = ""
	a.mu.Unlock()
	a.emit(AdapterEvent{Kind: "connected"})
}

func (a *Adapter) handleRawEvent(ctx context.Context, data []byte) {
	a.mu.RLock()
	selfID := a.status.SelfID
	a.mu.RUnlock()
	event, err := ParseEvent(data, selfID)
	if err != nil {
		a.emit(AdapterEvent{Kind: "error", Code: "invalid-event"})
		return
	}
	if event.Kind == "ignored" {
		return
	}
	a.mu.Lock()
	a.status.LastEventAt = nowText()
	if event.Kind == "heartbeat" {
		a.status.LastHeartbeatAt = nowText()
		a.status.HeartbeatIntervalMS = event.HeartbeatInterval
		a.status.HeartbeatOnline = event.HeartbeatOnline
		a.status.HeartbeatGood = event.HeartbeatGood
	}
	config := cloneConfig(a.config)
	if selfID == "" {
		selfID = event.Message.Address.AccountID
	}
	a.mu.Unlock()
	if event.Kind == "heartbeat" {
		a.emit(AdapterEvent{Kind: "heartbeat"})
		return
	}
	message := event.Message
	if !a.authorized(config, event) {
		a.emit(messageEvent("rejected", event, "unauthorized"))
		return
	}
	if event.ConversationType == "group" {
		text, triggered := groupText(config, message.Text, event.MentionedSelf)
		if !triggered {
			a.emit(messageEvent("rejected", event, "not-triggered"))
			return
		}
		message.Text = text
	}
	if strings.TrimSpace(message.Text) == "" {
		a.emit(messageEvent("rejected", event, "empty"))
		return
	}
	key := strings.Join([]string{selfID, event.ConversationType, event.ChatID, event.MessageID}, "\x00")
	if a.seen.Seen(key, time.Now()) {
		a.emit(messageEvent("rejected", event, "duplicate"))
		return
	}
	a.emit(messageEvent("message_received", event, ""))
	if a.handler != nil {
		a.handler(ctx, message)
	}
}

func (a *Adapter) authorized(config ConfigureRequest, event ParsedEvent) bool {
	if event.ConversationType == "private" {
		return contains(config.AllowedPrivateUserIDs, event.UserID)
	}
	return contains(config.AllowedGroupIDs, event.ChatID) && contains(config.AllowedGroupUserIDs, event.UserID)
}

func (a *Adapter) emit(event AdapterEvent) {
	a.mu.RLock()
	handler := a.events
	a.mu.RUnlock()
	if handler != nil {
		handler(event)
	}
}

func (a *Adapter) refreshStatusLocked() {
	a.status.Configured = hasUsableAllowlist(a.config)
	a.status.WebSocketURL = a.config.WebSocketURL
	a.status.AllowedPrivateUserCount = len(a.config.AllowedPrivateUserIDs)
	a.status.AllowedGroupCount = len(a.config.AllowedGroupIDs)
	a.status.AllowedGroupUserCount = len(a.config.AllowedGroupUserIDs)
	a.status.TokenSet = a.token != ""
	a.status.TokenFingerprint = tokenFingerprint(a.token)
	a.status.AutoStart = a.config.AutoStart
	a.status.ReconnectEnabled = a.config.ReconnectEnabled
	a.status.SendProgressUpdates = a.config.SendProgressUpdates
	a.status.Enabled = a.config.Enabled
	a.status.GroupTriggerMode = a.config.GroupTriggerMode
	a.status.CommandPrefix = a.config.CommandPrefix
}

func (a *Adapter) setErrorLocked(code string, err error) {
	a.status.LastErrorCode = code
	a.status.LastErrorMessage = safeErrorText(err, a.token)
}

func (a *Adapter) secret() string {
	a.mu.RLock()
	defer a.mu.RUnlock()
	return a.token
}

func normalizeConfigureRequest(request ConfigureRequest) (ConfigureRequest, error) {
	webSocketURL, err := NormalizeWebSocketURL(request.WebSocketURL)
	if err != nil {
		return ConfigureRequest{}, err
	}
	request.WebSocketURL = webSocketURL
	if request.GroupTriggerMode == "" {
		request.GroupTriggerMode = GroupTriggerMentionOnly
	}
	request.GroupTriggerMode = strings.ToLower(strings.TrimSpace(request.GroupTriggerMode))
	if request.GroupTriggerMode != GroupTriggerMentionOnly && request.GroupTriggerMode != GroupTriggerMentionOrPrefix {
		return ConfigureRequest{}, errors.New("groupTriggerMode must be mention-only or mention-or-prefix")
	}
	request.CommandPrefix = strings.TrimSpace(request.CommandPrefix)
	if request.CommandPrefix == "" {
		request.CommandPrefix = defaultCommandPrefix
	}
	if utf8.RuneCountInString(request.CommandPrefix) > 64 || strings.ContainsAny(request.CommandPrefix, "\r\n") {
		return ConfigureRequest{}, errors.New("commandPrefix must be one line with at most 64 characters")
	}
	if request.Token != nil {
		token := strings.TrimSpace(*request.Token)
		if token == "" {
			return ConfigureRequest{}, errors.New("accessToken cannot be empty; use ClearToken")
		}
		request.Token = &token
	}
	if request.AllowedPrivateUserIDs, err = normalizeIDs("allowedPrivateUserIds", request.AllowedPrivateUserIDs); err != nil {
		return ConfigureRequest{}, err
	}
	if request.AllowedGroupIDs, err = normalizeIDs("allowedGroupIds", request.AllowedGroupIDs); err != nil {
		return ConfigureRequest{}, err
	}
	if request.AllowedGroupUserIDs, err = normalizeIDs("allowedGroupUserIds", request.AllowedGroupUserIDs); err != nil {
		return ConfigureRequest{}, err
	}
	return request, nil
}

func normalizeIDs(field string, values []string) ([]string, error) {
	result := make([]string, 0, len(values))
	seen := make(map[string]struct{}, len(values))
	for _, value := range values {
		value = strings.TrimSpace(value)
		if !positiveID(value) {
			return nil, fmt.Errorf("%s must contain positive integer strings", field)
		}
		if _, duplicate := seen[value]; duplicate {
			continue
		}
		seen[value] = struct{}{}
		result = append(result, value)
	}
	return result, nil
}

func hasUsableAllowlist(config ConfigureRequest) bool {
	return config.WebSocketURL != "" && (len(config.AllowedPrivateUserIDs) > 0 || (len(config.AllowedGroupIDs) > 0 && len(config.AllowedGroupUserIDs) > 0))
}

func cloneConfig(config ConfigureRequest) ConfigureRequest {
	config.Token = nil
	config.AllowedPrivateUserIDs = append([]string(nil), config.AllowedPrivateUserIDs...)
	config.AllowedGroupIDs = append([]string(nil), config.AllowedGroupIDs...)
	config.AllowedGroupUserIDs = append([]string(nil), config.AllowedGroupUserIDs...)
	return config
}

func groupText(config ConfigureRequest, text string, mentionedSelf bool) (string, bool) {
	text = strings.TrimSpace(text)
	withoutPrefix, hasPrefix := trimCommandPrefix(text, config.CommandPrefix)
	triggered := mentionedSelf
	if config.GroupTriggerMode == GroupTriggerMentionOrPrefix {
		triggered = triggered || hasPrefix
	}
	if !triggered {
		return "", false
	}
	if hasPrefix {
		text = withoutPrefix
	}
	return strings.TrimSpace(text), true
}

func trimCommandPrefix(text, prefix string) (string, bool) {
	text = strings.TrimLeft(text, " \t\r\n")
	if !strings.HasPrefix(text, prefix) {
		return text, false
	}
	rest := text[len(prefix):]
	if rest != "" {
		first, _ := utf8.DecodeRuneInString(rest)
		if first != utf8.RuneError && first != ' ' && first != '\t' && first != '\r' && first != '\n' {
			return text, false
		}
	}
	return strings.TrimSpace(rest), true
}

func messageEvent(kind string, event ParsedEvent, reason string) AdapterEvent {
	return AdapterEvent{
		Kind: kind, Reason: reason, ConversationType: event.ConversationType,
		ChatID: event.ChatID, UserID: event.UserID, MessageID: event.MessageID,
		Unsupported: event.Message.Unsupported,
	}
}

func tokenFingerprint(token string) string {
	if token == "" {
		return ""
	}
	digest := sha256.Sum256([]byte(token))
	return hex.EncodeToString(digest[:6])
}

func safeAdapterError(err error, token string) error {
	if err == nil {
		return nil
	}
	if errors.Is(err, ErrAuthentication) {
		return fmt.Errorf("%w: %s", ErrAuthentication, safeErrorText(err, token))
	}
	return errors.New(safeErrorText(err, token))
}

func safeErrorText(err error, token string) string {
	if err == nil {
		return ""
	}
	value := err.Error()
	if token != "" {
		value = strings.ReplaceAll(value, token, "[redacted]")
	}
	if len(value) > 512 {
		value = value[:512]
	}
	return value
}

func categoryMessage(category string) string {
	switch category {
	case "invalid_websocket_url":
		return "The OneBot WebSocket URL is invalid"
	case "connection_refused":
		return "The OneBot WebSocket connection was refused"
	case "connection_timeout":
		return "The OneBot WebSocket connection timed out"
	case "authentication_failed":
		return "OneBot rejected the access token"
	case "websocket_handshake_failed":
		return "The server did not complete a OneBot WebSocket handshake"
	case "onebot_action_timeout":
		return "OneBot did not answer the login action in time"
	case "invalid_onebot_response":
		return "OneBot returned an invalid action response"
	case "napcat_not_logged_in":
		return "NapCat is not logged in to QQ"
	case "connection_closed":
		return "The OneBot WebSocket connection closed"
	default:
		return "The QQ connection failed"
	}
}

func nowText() string { return time.Now().UTC().Format(time.RFC3339Nano) }

func waitContext(ctx context.Context, duration time.Duration) bool {
	timer := time.NewTimer(duration)
	defer timer.Stop()
	select {
	case <-timer.C:
		return true
	case <-ctx.Done():
		return false
	}
}

type dedupSet struct {
	mu       sync.Mutex
	capacity int
	ttl      time.Duration
	entries  map[string]time.Time
	order    []dedupEntry
}

type dedupEntry struct {
	key     string
	expires time.Time
}

func newDedupSet(capacity int, ttl time.Duration) *dedupSet {
	return &dedupSet{capacity: capacity, ttl: ttl, entries: make(map[string]time.Time, capacity)}
}

func (set *dedupSet) Seen(key string, now time.Time) bool {
	set.mu.Lock()
	defer set.mu.Unlock()
	set.expire(now)
	if expires, exists := set.entries[key]; exists && now.Before(expires) {
		return true
	}
	expires := now.Add(set.ttl)
	set.entries[key] = expires
	set.order = append(set.order, dedupEntry{key: key, expires: expires})
	for len(set.entries) > set.capacity && len(set.order) > 0 {
		oldest := set.order[0]
		set.order = set.order[1:]
		if current, exists := set.entries[oldest.key]; exists && current.Equal(oldest.expires) {
			delete(set.entries, oldest.key)
		}
	}
	return false
}

func (set *dedupSet) expire(now time.Time) {
	for len(set.order) > 0 && !now.Before(set.order[0].expires) {
		oldest := set.order[0]
		set.order = set.order[1:]
		if current, exists := set.entries[oldest.key]; exists && current.Equal(oldest.expires) {
			delete(set.entries, oldest.key)
		}
	}
}

func contains(values []string, target string) bool {
	for _, value := range values {
		if value == target {
			return true
		}
	}
	return false
}

// SplitMessage splits by rune count, preferring paragraph, line, sentence and
// word boundaries. Multi-part messages include a stable [i/n] prefix.
func SplitMessage(text string, maxRunes int) []string {
	if maxRunes < 1 {
		maxRunes = defaultMessageRunes
	}
	text = strings.TrimSpace(text)
	if text == "" {
		return nil
	}
	if utf8.RuneCountInString(text) <= maxRunes {
		return []string{text}
	}
	contentLimit := maxRunes - 6
	if contentLimit < 1 {
		contentLimit = 1
	}
	parts := splitContent(text, contentLimit)
	for {
		prefixRunes := utf8.RuneCountInString(fmt.Sprintf("[%d/%d] ", len(parts), len(parts)))
		nextLimit := maxRunes - prefixRunes
		if nextLimit < 1 {
			nextLimit = 1
		}
		if nextLimit == contentLimit {
			break
		}
		contentLimit = nextLimit
		parts = splitContent(text, contentLimit)
	}
	result := make([]string, len(parts))
	for index, part := range parts {
		result[index] = fmt.Sprintf("[%d/%d] %s", index+1, len(parts), part)
	}
	return result
}

func splitContent(text string, limit int) []string {
	runes := []rune(strings.TrimSpace(text))
	parts := make([]string, 0, len(runes)/limit+1)
	for len(runes) > limit {
		cut := preferredCut(runes, limit)
		part := strings.TrimSpace(string(runes[:cut]))
		if part != "" {
			parts = append(parts, part)
		}
		runes = []rune(strings.TrimLeft(string(runes[cut:]), " \t\r\n"))
	}
	if part := strings.TrimSpace(string(runes)); part != "" {
		parts = append(parts, part)
	}
	return parts
}

func preferredCut(runes []rune, limit int) int {
	minimum := limit / 2
	for index := limit; index > minimum; index-- {
		if index >= 2 && runes[index-2] == '\n' && runes[index-1] == '\n' {
			return index
		}
	}
	for index := limit; index > minimum; index-- {
		if runes[index-1] == '\n' {
			return index
		}
	}
	for index := limit; index > minimum; index-- {
		switch runes[index-1] {
		case '.', '!', '?', '。', '！', '？':
			return index
		}
	}
	for index := limit; index > minimum; index-- {
		if runes[index-1] == ' ' || runes[index-1] == '\t' {
			return index
		}
	}
	return limit
}
