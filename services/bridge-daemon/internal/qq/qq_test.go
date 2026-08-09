package qq

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
	"unicode/utf8"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/channels"
	"github.com/gorilla/websocket"
)

func TestNormalizeWebSocketURLRejectsSecretBearingURLs(t *testing.T) {
	for _, value := range []string{
		"http://127.0.0.1:3001", "ws://user:secret@127.0.0.1:3001",
		"ws://127.0.0.1:3001?access_token=secret", "ws://127.0.0.1:3001/#fragment",
	} {
		if _, err := NormalizeWebSocketURL(value); err == nil {
			t.Fatalf("expected URL to be rejected: %s", value)
		}
	}
	if value, err := NormalizeWebSocketURL(""); err != nil || value != DefaultWebSocketURL {
		t.Fatalf("unexpected default URL: %q err=%v", value, err)
	}
}

func TestClientUsesBearerAndMatchesConcurrentEchoes(t *testing.T) {
	fake := newFakeOneBot(t, "top-secret", nil)
	defer fake.Close()
	client, err := NewClient(fake.URL(), "top-secret", nil)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	login, _, err := client.Start(ctx)
	if err != nil || login.UserID != "12345678901234567890" {
		t.Fatalf("start: login=%#v err=%v", login, err)
	}
	var wait sync.WaitGroup
	errorsByID := make(chan error, 2)
	for _, id := range []string{"slow", "fast"} {
		id := id
		wait.Add(1)
		go func() {
			defer wait.Done()
			var result struct {
				ID string `json:"id"`
			}
			if err := client.Call(ctx, "echo_test", map[string]any{"id": id}, &result); err != nil {
				errorsByID <- err
				return
			}
			if result.ID != id {
				errorsByID <- errors.New("response was delivered to the wrong echo waiter")
			}
		}()
	}
	wait.Wait()
	close(errorsByID)
	for err := range errorsByID {
		t.Error(err)
	}
	_ = client.Close()
}

func TestClientTimeoutRemovesPendingAndDisconnectCancelsPending(t *testing.T) {
	fake := newFakeOneBot(t, "", func(_ *fakeConnection, action Action) bool {
		return action.Action == "never_respond"
	})
	defer fake.Close()
	client, err := NewClient(fake.URL(), "", nil)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, _, err := client.Start(ctx); err != nil {
		t.Fatal(err)
	}
	timeoutContext, cancelTimeout := context.WithTimeout(ctx, 20*time.Millisecond)
	err = client.Call(timeoutContext, "never_respond", map[string]any{}, nil)
	cancelTimeout()
	if !errors.Is(err, ErrActionTimeout) || ClassifyError(err) != "onebot_action_timeout" {
		t.Fatalf("expected timeout, got %v", err)
	}
	client.mu.RLock()
	pending := len(client.pending)
	client.mu.RUnlock()
	if pending != 0 {
		t.Fatalf("timed-out pending action leaked: %d", pending)
	}
	waiting := make(chan error, 1)
	go func() { waiting <- client.Call(ctx, "never_respond", map[string]any{}, nil) }()
	time.Sleep(10 * time.Millisecond)
	_ = client.Close()
	select {
	case err := <-waiting:
		if !errors.Is(err, context.Canceled) && !errors.Is(err, ErrDisconnected) {
			t.Fatalf("unexpected disconnect error: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("pending action was not canceled by disconnect")
	}
}

func TestParseMessageSegmentsPreservesIDsAndSafeMentions(t *testing.T) {
	raw := []byte(`{
		"time":1710000000,"self_id":90071992547409931234,"post_type":"message","message_type":"group",
		"message_id":"90071992547409939999","group_id":12345678901234567890,"user_id":"42",
		"message":[{"type":"at","data":{"qq":"90071992547409931234"}},{"type":"text","data":{"text":" hello "}},{"type":"at","data":{"qq":"all"}},{"type":"image","data":{"url":"https://example.invalid/private"}}]
	}`)
	event, err := ParseEvent(raw, "90071992547409931234")
	if err != nil {
		t.Fatal(err)
	}
	if event.Kind != "message" || !event.MentionedSelf || event.ChatID != "12345678901234567890" || event.MessageID != "90071992547409939999" {
		t.Fatalf("unexpected parsed event: %#v", event)
	}
	if !event.Message.Unsupported || event.Message.Action != "unsupported-attachment" || !strings.Contains(event.Message.Text, "@all") || strings.Contains(event.Message.Text, "example.invalid") {
		t.Fatalf("unsafe or incomplete message extraction: %#v", event.Message)
	}
}

func TestParseEventFiltersSelfAndMessageSent(t *testing.T) {
	for _, raw := range []string{
		`{"post_type":"message","message_type":"private","self_id":"10","user_id":"10","message_id":"1","message":[{"type":"text","data":{"text":"self"}}]}`,
		`{"post_type":"message_sent","message_type":"private","self_id":"10","user_id":"20","message_id":"2","message":[{"type":"text","data":{"text":"sent"}}]}`,
	} {
		event, err := ParseEvent([]byte(raw), "10")
		if err != nil || event.Kind != "ignored" {
			t.Fatalf("event should be ignored: %#v err=%v", event, err)
		}
	}
}

func TestAdapterFiltersAllowlistTriggersAndDuplicates(t *testing.T) {
	var mu sync.Mutex
	received := []channels.InboundMessage{}
	adapter := NewAdapter(func(_ context.Context, message channels.InboundMessage) {
		mu.Lock()
		received = append(received, message)
		mu.Unlock()
	})
	_, err := adapter.Configure(ConfigureRequest{
		Enabled: true, AllowedPrivateUserIDs: []string{"20"}, AllowedGroupIDs: []string{"30"},
		AllowedGroupUserIDs: []string{"20"}, GroupTriggerMode: GroupTriggerMentionOrPrefix,
	})
	if err != nil {
		t.Fatal(err)
	}
	adapter.mu.Lock()
	adapter.status.SelfID = "10"
	adapter.mu.Unlock()
	private := `{"post_type":"message","message_type":"private","self_id":"10","user_id":"20","message_id":"1","message":[{"type":"text","data":{"text":"hello"}}]}`
	unauthorized := `{"post_type":"message","message_type":"private","self_id":"10","user_id":"21","message_id":"2","message":[{"type":"text","data":{"text":"no"}}]}`
	groupPrefix := `{"post_type":"message","message_type":"group","self_id":"10","group_id":"30","user_id":"20","message_id":"3","message":[{"type":"text","data":{"text":"/codex do it"}}]}`
	groupMention := `{"post_type":"message","message_type":"group","self_id":"10","group_id":"30","user_id":"20","message_id":"4","message":[{"type":"at","data":{"qq":"10"}},{"type":"text","data":{"text":" /codex again"}}]}`
	for _, raw := range []string{private, private, unauthorized, groupPrefix, groupMention} {
		adapter.handleRawEvent(context.Background(), []byte(raw))
	}
	mu.Lock()
	defer mu.Unlock()
	if len(received) != 3 {
		t.Fatalf("expected three authorized unique messages, got %#v", received)
	}
	if received[1].Text != "do it" || received[1].Address.ConversationType != "group" || received[2].Text != "again" {
		t.Fatalf("group trigger was not removed correctly: %#v", received)
	}
}

func TestAdapterReconnectsAndStopCancelsPromptly(t *testing.T) {
	var first atomic.Bool
	first.Store(true)
	fake := newFakeOneBot(t, "", func(connection *fakeConnection, action Action) bool {
		if action.Action == "get_version_info" && first.CompareAndSwap(true, false) {
			connection.closeAfterWrite.Store(true)
		}
		return false
	})
	defer fake.Close()
	adapter := NewAdapter(nil)
	adapter.backoffBase, adapter.backoffMax = 10*time.Millisecond, 20*time.Millisecond
	_, err := adapter.Configure(ConfigureRequest{WebSocketURL: fake.URL(), Enabled: true, ReconnectEnabled: true, AllowedPrivateUserIDs: []string{"20"}})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := adapter.Start(ctx); err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		status := adapter.QQStatus()
		if status.Connected && status.ReconnectCount > 0 {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}
	if status := adapter.QQStatus(); !status.Connected || status.ReconnectCount == 0 {
		t.Fatalf("adapter did not reconnect: %#v", status)
	}
	stopContext, cancelStop := context.WithTimeout(context.Background(), time.Second)
	defer cancelStop()
	if err := adapter.Stop(stopContext); err != nil {
		t.Fatal(err)
	}
	if status := adapter.QQStatus(); status.Running || status.ConnectionState != "stopped" || status.LastErrorMessage != "" {
		t.Fatalf("normal stop left an error state: %#v", status)
	}
}

func TestTestDoesNotLeakToken(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		http.Error(writer, "token is not accepted", http.StatusUnauthorized)
	}))
	defer server.Close()
	token := "never-print-this-token"
	adapter := NewAdapter(nil)
	result := adapter.Test(context.Background(), TestRequest{WebSocketURL: strings.Replace(server.URL, "http://", "ws://", 1), Token: &token})
	if result.OK || strings.Contains(result.Message, token) || result.Category != "authentication_failed" {
		t.Fatalf("unsafe test result: %#v", result)
	}
}

func TestHeartbeatAndUnicodeSplit(t *testing.T) {
	adapter := NewAdapter(nil)
	adapter.handleRawEvent(context.Background(), []byte(`{"post_type":"meta_event","meta_event_type":"heartbeat","time":1710000000,"interval":5000,"status":{"online":true,"good":true}}`))
	status := adapter.QQStatus()
	if status.HeartbeatIntervalMS != 5000 || !status.HeartbeatOnline || !status.HeartbeatGood || status.LastHeartbeatAt == "" {
		t.Fatalf("heartbeat was not recorded: %#v", status)
	}
	parts := SplitMessage("甲乙丙丁戊己庚辛壬癸", 8)
	if len(parts) < 2 {
		t.Fatalf("expected split message: %#v", parts)
	}
	for _, part := range parts {
		if utf8.RuneCountInString(part) > 8 {
			t.Fatalf("part exceeds rune limit: %q", part)
		}
	}
}

type fakeOneBot struct {
	t        *testing.T
	server   *httptest.Server
	token    string
	hook     func(*fakeConnection, Action) bool
	upgrader websocket.Upgrader
}

type fakeConnection struct {
	t               *testing.T
	conn            *websocket.Conn
	writeMu         sync.Mutex
	closeAfterWrite atomic.Bool
}

func newFakeOneBot(t *testing.T, token string, hook func(*fakeConnection, Action) bool) *fakeOneBot {
	t.Helper()
	fake := &fakeOneBot{t: t, token: token, hook: hook, upgrader: websocket.Upgrader{CheckOrigin: func(*http.Request) bool { return true }}}
	fake.server = httptest.NewServer(http.HandlerFunc(fake.serveHTTP))
	return fake
}

func (fake *fakeOneBot) URL() string {
	return strings.Replace(fake.server.URL, "http://", "ws://", 1)
}

func (fake *fakeOneBot) Close() { fake.server.Close() }

func (fake *fakeOneBot) serveHTTP(writer http.ResponseWriter, request *http.Request) {
	if request.Header.Get("Authorization") != bearer(fake.token) {
		http.Error(writer, "unauthorized", http.StatusUnauthorized)
		return
	}
	conn, err := fake.upgrader.Upgrade(writer, request, nil)
	if err != nil {
		return
	}
	defer conn.Close()
	connection := &fakeConnection{t: fake.t, conn: conn}
	for {
		var action Action
		if err := conn.ReadJSON(&action); err != nil {
			return
		}
		if fake.hook != nil && fake.hook(connection, action) {
			continue
		}
		data := any(map[string]any{})
		delay := time.Duration(0)
		switch action.Action {
		case "get_login_info":
			data = map[string]any{"user_id": "12345678901234567890", "nickname": "Codex"}
		case "get_version_info":
			data = map[string]any{"protocol_version": "11", "app_name": "NapCat", "app_version": "4.8.0"}
		case "echo_test":
			params, _ := action.Params.(map[string]any)
			id, _ := params["id"].(string)
			data = map[string]any{"id": id}
			if id == "slow" {
				delay = 20 * time.Millisecond
			}
		case "send_private_msg", "send_group_msg":
			params, _ := action.Params.(map[string]any)
			segments, _ := params["message"].([]any)
			if len(segments) == 0 {
				fake.t.Error("outbound message did not use a segment array")
			}
			data = map[string]any{"message_id": "99"}
		}
		if delay > 0 {
			time.Sleep(delay)
		}
		connection.writeMu.Lock()
		err := conn.WriteJSON(map[string]any{"status": "ok", "retcode": 0, "data": data, "echo": action.Echo})
		connection.writeMu.Unlock()
		if err != nil {
			return
		}
		if connection.closeAfterWrite.Load() {
			return
		}
	}
}

func bearer(token string) string {
	if token == "" {
		return ""
	}
	return "Bearer " + token
}

func TestIDRejectsFractionalNumbers(t *testing.T) {
	var id ID
	if err := json.Unmarshal([]byte(`1.25`), &id); err == nil {
		t.Fatal("fractional OneBot ID was accepted")
	}
}
