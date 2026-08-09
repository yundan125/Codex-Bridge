package qq

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"html"
	"strconv"
	"strings"
	"time"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/channels"
)

// ID preserves OneBot identifiers exactly whether the peer encoded them as a
// JSON string or number.
type ID string

func (id *ID) UnmarshalJSON(data []byte) error {
	raw := strings.TrimSpace(string(data))
	if raw == "" || raw == "null" {
		*id = ""
		return nil
	}
	if raw[0] == '"' {
		var value string
		if err := json.Unmarshal(data, &value); err != nil {
			return err
		}
		*id = ID(strings.TrimSpace(value))
		return nil
	}
	if !isInteger(raw) {
		return fmt.Errorf("OneBot ID must be an integer or string")
	}
	*id = ID(raw)
	return nil
}

func (id ID) MarshalJSON() ([]byte, error) {
	return json.Marshal(string(id))
}

type Action struct {
	Action string `json:"action"`
	Params any    `json:"params"`
	Echo   string `json:"echo"`
}

type actionResponse struct {
	Status  string          `json:"status"`
	RetCode int             `json:"retcode"`
	Data    json.RawMessage `json:"data"`
	Message string          `json:"message"`
	Wording string          `json:"wording"`
	Echo    ID              `json:"echo"`
}

type LoginInfo struct {
	UserID   ID     `json:"user_id"`
	Nickname string `json:"nickname"`
}

type VersionInfo struct {
	AppName               string `json:"app_name"`
	AppVersion            string `json:"app_version"`
	ProtocolVersion       string `json:"protocol_version"`
	Implementation        string `json:"implementation"`
	ImplementationVersion string `json:"implementation_version"`
}

type SendMessageResult struct {
	MessageID ID `json:"message_id"`
}

type MessageSegment struct {
	Type string         `json:"type"`
	Data map[string]any `json:"data"`
}

type rawEventHeader struct {
	PostType    string `json:"post_type"`
	MessageType string `json:"message_type"`
	MetaType    string `json:"meta_event_type"`
}

type rawMessageEvent struct {
	PostType    string          `json:"post_type"`
	MessageType string          `json:"message_type"`
	SubType     string          `json:"sub_type"`
	SelfID      ID              `json:"self_id"`
	UserID      ID              `json:"user_id"`
	GroupID     ID              `json:"group_id"`
	MessageID   ID              `json:"message_id"`
	Time        json.Number     `json:"time"`
	RawMessage  string          `json:"raw_message"`
	Message     json.RawMessage `json:"message"`
	Sender      struct {
		UserID ID `json:"user_id"`
	} `json:"sender"`
}

type rawHeartbeatEvent struct {
	PostType string      `json:"post_type"`
	MetaType string      `json:"meta_event_type"`
	SelfID   ID          `json:"self_id"`
	Time     json.Number `json:"time"`
	Interval int64       `json:"interval"`
	Status   struct {
		Online bool `json:"online"`
		Good   bool `json:"good"`
	} `json:"status"`
}

type ParsedEvent struct {
	Kind              string
	Message           channels.InboundMessage
	MentionedSelf     bool
	ConversationType  string
	ChatID            string
	UserID            string
	MessageID         string
	EventTime         time.Time
	HeartbeatInterval int64
	HeartbeatOnline   bool
	HeartbeatGood     bool
}

func ParseEvent(data []byte, expectedSelfID string) (ParsedEvent, error) {
	var header rawEventHeader
	if err := json.Unmarshal(data, &header); err != nil {
		return ParsedEvent{}, fmt.Errorf("decode OneBot event: %w", err)
	}
	if header.PostType == "meta_event" && header.MetaType == "heartbeat" {
		var heartbeat rawHeartbeatEvent
		if err := json.Unmarshal(data, &heartbeat); err != nil {
			return ParsedEvent{}, fmt.Errorf("decode OneBot heartbeat: %w", err)
		}
		return ParsedEvent{
			Kind: "heartbeat", EventTime: oneBotTime(heartbeat.Time),
			HeartbeatInterval: heartbeat.Interval, HeartbeatOnline: heartbeat.Status.Online,
			HeartbeatGood: heartbeat.Status.Good,
		}, nil
	}
	if header.PostType != "message" {
		return ParsedEvent{Kind: "ignored"}, nil
	}
	var event rawMessageEvent
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if err := decoder.Decode(&event); err != nil {
		return ParsedEvent{}, fmt.Errorf("decode OneBot message: %w", err)
	}
	selfID := strings.TrimSpace(expectedSelfID)
	if selfID == "" {
		selfID = string(event.SelfID)
	}
	senderID := string(event.UserID)
	if senderID == "" {
		senderID = string(event.Sender.UserID)
	}
	if selfID == "" || senderID == "" || senderID == selfID {
		return ParsedEvent{Kind: "ignored"}, nil
	}
	conversationType := strings.ToLower(strings.TrimSpace(event.MessageType))
	chatID := senderID
	if conversationType == "group" {
		chatID = string(event.GroupID)
	}
	if (conversationType != "private" && conversationType != "group") || chatID == "" || string(event.MessageID) == "" {
		return ParsedEvent{Kind: "ignored"}, nil
	}
	text, mentionedSelf, unsupported, action, err := parseMessage(event.Message, event.RawMessage, selfID)
	if err != nil {
		return ParsedEvent{}, err
	}
	text = strings.TrimSpace(text)
	if text == "" && unsupported {
		text = "[Unsupported attachment]"
	}
	if text == "" {
		return ParsedEvent{Kind: "ignored"}, nil
	}
	received := oneBotTime(event.Time)
	if received.IsZero() {
		received = time.Now().UTC()
	}
	message := channels.InboundMessage{
		Address: channels.ChannelAddress{
			ChannelType: "qq", AccountID: selfID, ConversationType: conversationType,
			ChatID: chatID, UserID: senderID,
		},
		MessageID: string(event.MessageID), UserID: senderID, Text: text,
		Action: action, Unsupported: unsupported, Received: received,
	}
	if value, err := strconv.ParseInt(string(event.MessageID), 10, 64); err == nil {
		message.UpdateID = value
	}
	return ParsedEvent{
		Kind: "message", Message: message, MentionedSelf: mentionedSelf,
		ConversationType: conversationType, ChatID: chatID, UserID: senderID,
		MessageID: string(event.MessageID), EventTime: received,
	}, nil
}

func parseMessage(raw json.RawMessage, fallback, selfID string) (text string, mentionedSelf, unsupported bool, action string, err error) {
	trimmed := bytes.TrimSpace(raw)
	if len(trimmed) > 0 && trimmed[0] == '[' {
		var segments []struct {
			Type string                     `json:"type"`
			Data map[string]json.RawMessage `json:"data"`
		}
		if err := json.Unmarshal(trimmed, &segments); err != nil {
			return "", false, false, "", fmt.Errorf("decode OneBot message segments: %w", err)
		}
		var builder strings.Builder
		for _, segment := range segments {
			typeName := strings.ToLower(strings.TrimSpace(segment.Type))
			switch typeName {
			case "text":
				builder.WriteString(rawString(segment.Data["text"]))
			case "at":
				qqID := rawID(segment.Data["qq"])
				if qqID == selfID {
					mentionedSelf = true
					continue
				}
				if qqID != "" {
					builder.WriteString(" @" + qqID + " ")
				}
			case "reply":
				// Reply metadata is routing context, not user-authored text.
			case "image", "record", "video", "file":
				unsupported = true
			case "json", "xml":
				unsupported = true
			default:
				if typeName != "" {
					unsupported = true
				}
			}
		}
		if unsupported {
			action = "unsupported-attachment"
		}
		return builder.String(), mentionedSelf, unsupported, action, nil
	}
	if len(trimmed) > 0 && trimmed[0] == '"' {
		if err := json.Unmarshal(trimmed, &fallback); err != nil {
			return "", false, false, "", fmt.Errorf("decode OneBot string message: %w", err)
		}
	}
	return parseCQMessage(fallback, selfID)
}

func parseCQMessage(value, selfID string) (text string, mentionedSelf, unsupported bool, action string, err error) {
	var builder strings.Builder
	for len(value) > 0 {
		start := strings.Index(value, "[CQ:")
		if start < 0 {
			builder.WriteString(cqUnescape(value))
			break
		}
		builder.WriteString(cqUnescape(value[:start]))
		endRelative := strings.IndexByte(value[start:], ']')
		if endRelative < 0 {
			builder.WriteString(cqUnescape(value[start:]))
			break
		}
		end := start + endRelative
		tag := value[start+4 : end]
		parts := strings.Split(tag, ",")
		typeName := strings.ToLower(strings.TrimSpace(parts[0]))
		attributes := make(map[string]string, len(parts)-1)
		for _, part := range parts[1:] {
			key, rawValue, ok := strings.Cut(part, "=")
			if ok {
				attributes[strings.TrimSpace(key)] = cqUnescape(rawValue)
			}
		}
		switch typeName {
		case "at":
			qqID := strings.TrimSpace(attributes["qq"])
			if qqID == selfID {
				mentionedSelf = true
			} else if qqID != "" {
				builder.WriteString(" @" + qqID + " ")
			}
		case "reply":
		case "image", "record", "video", "file", "json", "xml":
			unsupported = true
		default:
			unsupported = true
		}
		value = value[end+1:]
	}
	if unsupported {
		action = "unsupported-attachment"
	}
	return builder.String(), mentionedSelf, unsupported, action, nil
}

func rawString(raw json.RawMessage) string {
	var value string
	_ = json.Unmarshal(raw, &value)
	return value
}

func rawID(raw json.RawMessage) string {
	var value ID
	if err := json.Unmarshal(raw, &value); err != nil {
		return ""
	}
	return string(value)
}

func oneBotTime(number json.Number) time.Time {
	seconds, err := strconv.ParseInt(string(number), 10, 64)
	if err != nil || seconds <= 0 {
		return time.Time{}
	}
	return time.Unix(seconds, 0).UTC()
}

func cqUnescape(value string) string {
	value = strings.ReplaceAll(value, "&#44;", ",")
	value = strings.ReplaceAll(value, "&#91;", "[")
	value = strings.ReplaceAll(value, "&#93;", "]")
	return html.UnescapeString(value)
}

func isInteger(value string) bool {
	if value == "" {
		return false
	}
	for index, char := range value {
		if index == 0 && char == '-' {
			continue
		}
		if char < '0' || char > '9' {
			return false
		}
	}
	return value != "-"
}

func positiveID(value string) bool {
	value = strings.TrimSpace(value)
	if value == "" || value == "0" {
		return false
	}
	for _, char := range value {
		if char < '0' || char > '9' {
			return false
		}
	}
	return true
}

var errEmptyMessage = errors.New("QQ message is empty")
