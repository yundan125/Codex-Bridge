package qq

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"
)

const DefaultWebSocketURL = "ws://127.0.0.1:3001"

var (
	ErrAuthentication      = errors.New("OneBot authentication failed")
	ErrDisconnected        = errors.New("OneBot connection is not available")
	ErrInvalidWebSocketURL = errors.New("invalid OneBot WebSocket URL")
	ErrWebSocketHandshake  = errors.New("OneBot WebSocket handshake failed")
	ErrActionTimeout       = errors.New("OneBot action timed out")
	ErrInvalidResponse     = errors.New("invalid OneBot response")
	ErrNotLoggedIn         = errors.New("NapCat is not logged in")
	ErrHeartbeatTimeout    = errors.New("OneBot connection became idle")
	clientGeneration       atomic.Uint64
)

type ActionError struct {
	Action  string
	RetCode int
	Message string
}

func (e *ActionError) Error() string {
	if e.Message == "" {
		return fmt.Sprintf("OneBot action %s failed with retcode %d", e.Action, e.RetCode)
	}
	return fmt.Sprintf("OneBot action %s failed with retcode %d: %s", e.Action, e.RetCode, e.Message)
}

type callResult struct {
	response actionResponse
	err      error
}

type Client struct {
	mu          sync.RWMutex
	writeMu     sync.Mutex
	url         string
	token       string
	dialer      *websocket.Dialer
	conn        *websocket.Conn
	ctx         context.Context
	cancel      context.CancelFunc
	done        chan error
	events      chan []byte
	handler     func([]byte)
	pending     map[string]chan callResult
	generation  uint64
	sequence    atomic.Uint64
	lastInbound atomic.Int64
	forcedErr   error
	started     bool
	starting    bool
	closing     bool
}

func NewClient(webSocketURL, accessToken string, handler func([]byte)) (*Client, error) {
	normalizedURL, err := NormalizeWebSocketURL(webSocketURL)
	if err != nil {
		return nil, err
	}
	return &Client{
		url: normalizedURL, token: strings.TrimSpace(accessToken), handler: handler,
		dialer: websocket.DefaultDialer, pending: make(map[string]chan callResult),
	}, nil
}

func NormalizeWebSocketURL(value string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		value = DefaultWebSocketURL
	}
	parsed, err := url.Parse(value)
	if err != nil {
		return "", fmt.Errorf("%w: webSocketUrl is invalid", ErrInvalidWebSocketURL)
	}
	if parsed.Scheme != "ws" && parsed.Scheme != "wss" {
		return "", fmt.Errorf("%w: webSocketUrl must use ws or wss", ErrInvalidWebSocketURL)
	}
	if parsed.Host == "" {
		return "", fmt.Errorf("%w: webSocketUrl must include a host", ErrInvalidWebSocketURL)
	}
	if parsed.User != nil || parsed.RawQuery != "" || parsed.Fragment != "" {
		return "", fmt.Errorf("%w: webSocketUrl must not contain credentials, query parameters, or fragments", ErrInvalidWebSocketURL)
	}
	return parsed.String(), nil
}

// Start establishes the Forward WebSocket, starts the sole reader before any
// action is issued, and identifies the OneBot account.
func (c *Client) Start(parent context.Context) (LoginInfo, VersionInfo, error) {
	c.mu.Lock()
	if c.started || c.starting {
		c.mu.Unlock()
		return LoginInfo{}, VersionInfo{}, errors.New("OneBot client is already started")
	}
	c.starting = true
	c.mu.Unlock()

	header := make(http.Header)
	if c.token != "" {
		header.Set("Authorization", "Bearer "+c.token)
	}
	conn, response, err := c.dialer.DialContext(parent, c.url, header)
	if err != nil {
		c.mu.Lock()
		c.starting = false
		c.mu.Unlock()
		if response != nil && (response.StatusCode == http.StatusUnauthorized || response.StatusCode == http.StatusForbidden) {
			return LoginInfo{}, VersionInfo{}, fmt.Errorf("%w: websocket handshake status %d", ErrAuthentication, response.StatusCode)
		}
		if response != nil {
			return LoginInfo{}, VersionInfo{}, fmt.Errorf("%w: status %d", ErrWebSocketHandshake, response.StatusCode)
		}
		return LoginInfo{}, VersionInfo{}, fmt.Errorf("connect to OneBot Forward WebSocket: %w", err)
	}
	ctx, cancel := context.WithCancel(parent)
	c.mu.Lock()
	c.ctx = ctx
	c.cancel = cancel
	c.conn = conn
	c.done = make(chan error, 1)
	c.events = make(chan []byte, 256)
	c.generation = clientGeneration.Add(1)
	c.lastInbound.Store(time.Now().UnixNano())
	c.forcedErr = nil
	c.started = true
	c.starting = false
	c.closing = false
	c.mu.Unlock()
	conn.SetReadLimit(1 << 20)
	go c.dispatchLoop(ctx)
	go c.readLoop(conn)
	go func() {
		<-ctx.Done()
		_ = c.Close()
	}()

	loginContext, cancelLogin := context.WithTimeout(ctx, 10*time.Second)
	var login LoginInfo
	err = c.Call(loginContext, "get_login_info", map[string]any{}, &login)
	cancelLogin()
	if err != nil {
		_ = c.Close()
		return LoginInfo{}, VersionInfo{}, err
	}
	if !positiveID(string(login.UserID)) {
		_ = c.Close()
		return LoginInfo{}, VersionInfo{}, fmt.Errorf("%w: get_login_info returned an invalid user_id", ErrNotLoggedIn)
	}
	versionContext, cancelVersion := context.WithTimeout(ctx, 2*time.Second)
	var version VersionInfo
	if err := c.Call(versionContext, "get_version_info", map[string]any{}, &version); err == nil {
		if version.Implementation == "" {
			version.Implementation = version.AppName
		}
		if version.ImplementationVersion == "" {
			version.ImplementationVersion = version.AppVersion
		}
	}
	cancelVersion()
	return login, version, nil
}

func (c *Client) Done() <-chan error {
	c.mu.RLock()
	done := c.done
	c.mu.RUnlock()
	return done
}

func (c *Client) Call(ctx context.Context, action string, params, target any) error {
	action = strings.TrimSpace(action)
	if action == "" {
		return errors.New("OneBot action is required")
	}
	c.mu.RLock()
	conn := c.conn
	generation := c.generation
	started := c.started && !c.closing
	c.mu.RUnlock()
	if !started || conn == nil {
		return ErrDisconnected
	}
	echo := fmt.Sprintf("g%d-a%d", generation, c.sequence.Add(1))
	result := make(chan callResult, 1)
	c.mu.Lock()
	if c.conn != conn || c.closing {
		c.mu.Unlock()
		return ErrDisconnected
	}
	c.pending[echo] = result
	c.mu.Unlock()

	c.writeMu.Lock()
	_ = conn.SetWriteDeadline(time.Now().Add(10 * time.Second))
	err := conn.WriteJSON(Action{Action: action, Params: params, Echo: echo})
	_ = conn.SetWriteDeadline(time.Time{})
	c.writeMu.Unlock()
	if err != nil {
		c.removePending(echo)
		return fmt.Errorf("write OneBot action %s: %w", action, err)
	}
	select {
	case response := <-result:
		if response.err != nil {
			return response.err
		}
		if !strings.EqualFold(response.response.Status, "ok") || response.response.RetCode != 0 {
			message := strings.TrimSpace(response.response.Message)
			if message == "" {
				message = strings.TrimSpace(response.response.Wording)
			}
			if len(message) > 256 {
				message = message[:256]
			}
			return &ActionError{Action: action, RetCode: response.response.RetCode, Message: message}
		}
		if target != nil && len(response.response.Data) > 0 && string(response.response.Data) != "null" {
			if err := json.Unmarshal(response.response.Data, target); err != nil {
				return fmt.Errorf("%w for action %s: %v", ErrInvalidResponse, action, err)
			}
		}
		return nil
	case <-ctx.Done():
		c.removePending(echo)
		if errors.Is(ctx.Err(), context.DeadlineExceeded) {
			return fmt.Errorf("%w: %s", ErrActionTimeout, action)
		}
		return ctx.Err()
	}
}

// ClassifyError maps transport and OneBot failures to the stable API error
// vocabulary. It never includes endpoint or token material.
func ClassifyError(err error) string {
	if err == nil {
		return ""
	}
	switch {
	case errors.Is(err, ErrInvalidWebSocketURL):
		return "invalid_websocket_url"
	case errors.Is(err, ErrAuthentication):
		return "authentication_failed"
	case errors.Is(err, ErrWebSocketHandshake):
		return "websocket_handshake_failed"
	case errors.Is(err, ErrActionTimeout):
		return "onebot_action_timeout"
	case errors.Is(err, ErrInvalidResponse):
		return "invalid_onebot_response"
	case errors.Is(err, ErrNotLoggedIn):
		return "napcat_not_logged_in"
	case errors.Is(err, ErrDisconnected):
		return "connection_closed"
	case errors.Is(err, ErrHeartbeatTimeout):
		return "connection_closed"
	case errors.Is(err, context.DeadlineExceeded):
		return "connection_timeout"
	}
	var networkError net.Error
	if errors.As(err, &networkError) && networkError.Timeout() {
		return "connection_timeout"
	}
	var actionError *ActionError
	if errors.As(err, &actionError) {
		message := strings.ToLower(actionError.Message)
		if strings.Contains(message, "not logged in") || strings.Contains(message, "not login") {
			return "napcat_not_logged_in"
		}
		return "invalid_onebot_response"
	}
	message := strings.ToLower(err.Error())
	switch {
	case strings.Contains(message, "connection refused"), strings.Contains(message, "actively refused"):
		return "connection_refused"
	case strings.Contains(message, "bad handshake"):
		return "websocket_handshake_failed"
	case strings.Contains(message, "close "), strings.Contains(message, "closed network connection"), strings.Contains(message, "unexpected eof"), message == "eof":
		return "connection_closed"
	default:
		return "unknown_qq_error"
	}
}

func (c *Client) GetLoginInfo(ctx context.Context) (LoginInfo, error) {
	var result LoginInfo
	err := c.Call(ctx, "get_login_info", map[string]any{}, &result)
	return result, err
}

func (c *Client) GetVersionInfo(ctx context.Context) (VersionInfo, error) {
	var result VersionInfo
	err := c.Call(ctx, "get_version_info", map[string]any{}, &result)
	return result, err
}

func (c *Client) SendPrivateMessage(ctx context.Context, userID, text string) (SendMessageResult, error) {
	if !positiveID(userID) {
		return SendMessageResult{}, errors.New("QQ private user ID must be a positive integer string")
	}
	return c.send(ctx, "send_private_msg", map[string]any{"user_id": userID, "message": textSegments(text)})
}

func (c *Client) SendGroupMessage(ctx context.Context, groupID, text string) (SendMessageResult, error) {
	if !positiveID(groupID) {
		return SendMessageResult{}, errors.New("QQ group ID must be a positive integer string")
	}
	return c.send(ctx, "send_group_msg", map[string]any{"group_id": groupID, "message": textSegments(text)})
}

func (c *Client) send(ctx context.Context, action string, params map[string]any) (SendMessageResult, error) {
	segments, _ := params["message"].([]MessageSegment)
	if len(segments) == 0 || strings.TrimSpace(rawSegmentText(segments[0])) == "" {
		return SendMessageResult{}, errEmptyMessage
	}
	var result SendMessageResult
	err := c.Call(ctx, action, params, &result)
	return result, err
}

func (c *Client) Close() error {
	c.mu.Lock()
	if c.closing || c.conn == nil {
		c.mu.Unlock()
		return nil
	}
	c.closing = true
	cancel := c.cancel
	conn := c.conn
	c.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	c.failPending(context.Canceled)
	_ = conn.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(websocket.CloseNormalClosure, ""), time.Now().Add(time.Second))
	err := conn.Close()
	return err
}

// LastInboundAt reports when the reader last received any WebSocket frame.
// It is used by the adapter watchdog so a missing OneBot heartbeat does not
// leave a half-open Forward WebSocket marked connected forever.
func (c *Client) LastInboundAt() time.Time {
	value := c.lastInbound.Load()
	if value <= 0 {
		return time.Time{}
	}
	return time.Unix(0, value)
}

// Abort forces the current connection to fail without marking it as a normal
// user-requested Stop. The reader owns final cleanup and notifies Done once.
func (c *Client) Abort(err error) {
	if err == nil {
		err = ErrDisconnected
	}
	c.mu.Lock()
	if c.closing || c.conn == nil {
		c.mu.Unlock()
		return
	}
	c.forcedErr = err
	conn := c.conn
	c.mu.Unlock()
	c.failPending(err)
	_ = conn.Close()
}

func (c *Client) readLoop(conn *websocket.Conn) {
	var terminalErr error
	defer func() {
		c.mu.Lock()
		normal := c.closing
		if c.conn == conn {
			c.conn = nil
			c.started = false
		}
		done := c.done
		cancel := c.cancel
		forcedErr := c.forcedErr
		c.forcedErr = nil
		c.mu.Unlock()
		if cancel != nil {
			cancel()
		}
		if normal {
			terminalErr = nil
		} else if forcedErr != nil {
			terminalErr = forcedErr
		} else if terminalErr == nil {
			terminalErr = ErrDisconnected
		}
		c.failPending(terminalErr)
		if done != nil {
			done <- terminalErr
			close(done)
		}
	}()
	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			terminalErr = err
			return
		}
		c.lastInbound.Store(time.Now().UnixNano())
		var response actionResponse
		if err := json.Unmarshal(data, &response); err == nil && response.Echo != "" {
			c.deliverResponse(string(response.Echo), response)
			continue
		}
		c.mu.RLock()
		events := c.events
		ctx := c.ctx
		c.mu.RUnlock()
		if events == nil || ctx == nil {
			continue
		}
		copy := append([]byte(nil), data...)
		select {
		case events <- copy:
		case <-ctx.Done():
			return
		default:
			// A full event queue is dropped deliberately; responses never use it.
		}
	}
}

func (c *Client) dispatchLoop(ctx context.Context) {
	c.mu.RLock()
	events := c.events
	handler := c.handler
	c.mu.RUnlock()
	if handler == nil {
		return
	}
	for {
		select {
		case data := <-events:
			handler(data)
		case <-ctx.Done():
			return
		}
	}
}

func (c *Client) deliverResponse(echo string, response actionResponse) {
	c.mu.Lock()
	waiter, ok := c.pending[echo]
	if ok {
		delete(c.pending, echo)
	}
	c.mu.Unlock()
	if ok {
		waiter <- callResult{response: response}
	}
}

func (c *Client) removePending(echo string) {
	c.mu.Lock()
	delete(c.pending, echo)
	c.mu.Unlock()
}

func (c *Client) failPending(err error) {
	if err == nil {
		err = ErrDisconnected
	}
	c.mu.Lock()
	pending := c.pending
	c.pending = make(map[string]chan callResult)
	c.mu.Unlock()
	for _, waiter := range pending {
		waiter <- callResult{err: err}
	}
}

func textSegments(text string) []MessageSegment {
	if strings.TrimSpace(text) == "" {
		return nil
	}
	return []MessageSegment{{Type: "text", Data: map[string]any{"text": text}}}
}

func rawSegmentText(segment MessageSegment) string {
	value, _ := segment.Data["text"].(string)
	return value
}
