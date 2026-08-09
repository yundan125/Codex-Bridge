package threadregistry

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

type Metadata struct {
	ThreadID   string
	Title      string
	CWD        string
	CreatedAt  string
	LastSeenAt string
}

type Record struct {
	ThreadID   string `json:"threadId"`
	Number     int    `json:"number"`
	Title      string `json:"title"`
	CWD        string `json:"cwd"`
	CreatedAt  string `json:"createdAt"`
	LastSeenAt string `json:"lastSeenAt"`
}

type diskModel struct {
	Version    int      `json:"version"`
	NextNumber int      `json:"nextNumber"`
	Threads    []Record `json:"threads"`
}

type Registry struct {
	mu         sync.RWMutex
	path       string
	nextNumber int
	byThread   map[string]Record
	byNumber   map[int]string
}

func New(path string) (*Registry, error) {
	r := &Registry{path: path, nextNumber: 1, byThread: map[string]Record{}, byNumber: map[int]string{}}
	if err := r.load(); err != nil {
		return nil, err
	}
	return r, nil
}

func (r *Registry) EnsureBatch(values []Metadata) ([]Record, error) {
	values = append([]Metadata(nil), values...)
	sort.SliceStable(values, func(i, j int) bool {
		a, b := parseTime(values[i].CreatedAt), parseTime(values[j].CreatedAt)
		if a.Equal(b) {
			return values[i].ThreadID < values[j].ThreadID
		}
		if a.IsZero() {
			return false
		}
		if b.IsZero() {
			return true
		}
		return a.Before(b)
	})
	r.mu.Lock()
	defer r.mu.Unlock()
	changed := false
	now := time.Now().UTC().Format(time.RFC3339Nano)
	for _, value := range values {
		value.ThreadID = strings.TrimSpace(value.ThreadID)
		if value.ThreadID == "" {
			continue
		}
		record, ok := r.byThread[value.ThreadID]
		if !ok {
			record = Record{ThreadID: value.ThreadID, Number: r.nextNumber, CreatedAt: first(value.CreatedAt, now)}
			r.nextNumber++
			r.byNumber[record.Number] = record.ThreadID
			changed = true
		}
		lastSeen := first(value.LastSeenAt, now)
		if record.Title != strings.TrimSpace(value.Title) || record.CWD != strings.TrimSpace(value.CWD) || record.LastSeenAt != lastSeen {
			record.Title, record.CWD, record.LastSeenAt = strings.TrimSpace(value.Title), strings.TrimSpace(value.CWD), lastSeen
			changed = true
		}
		r.byThread[record.ThreadID] = record
	}
	if changed {
		if err := r.saveLocked(); err != nil {
			return nil, err
		}
	}
	result := make([]Record, 0, len(values))
	for _, value := range values {
		if record, ok := r.byThread[strings.TrimSpace(value.ThreadID)]; ok {
			result = append(result, record)
		}
	}
	return result, nil
}

func (r *Registry) Ensure(value Metadata) (Record, error) {
	values, err := r.EnsureBatch([]Metadata{value})
	if err != nil {
		return Record{}, err
	}
	if len(values) == 0 {
		return Record{}, errors.New("threadId is required")
	}
	return values[0], nil
}

func (r *Registry) ByThreadID(id string) (Record, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	value, ok := r.byThread[strings.TrimSpace(id)]
	return value, ok
}

func (r *Registry) ByNumber(number int) (Record, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	id, ok := r.byNumber[number]
	if !ok {
		return Record{}, false
	}
	return r.byThread[id], true
}

func (r *Registry) List() []Record {
	r.mu.RLock()
	defer r.mu.RUnlock()
	result := make([]Record, 0, len(r.byThread))
	for _, value := range r.byThread {
		result = append(result, value)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Number < result[j].Number })
	return result
}

// ParsePrefix recognizes the stable-number routing formats. recognized is true
// for syntactically explicit #n/[n] prefixes even when the number is unknown.
func (r *Registry) ParsePrefix(text string) (record Record, content string, recognized bool, err error) {
	trimmed := strings.TrimSpace(text)
	numberText, rest, explicit := "", "", false
	switch {
	case strings.HasPrefix(trimmed, "#"):
		explicit = true
		numberText, rest = takeNumber(trimmed[1:])
	case strings.HasPrefix(trimmed, "["):
		if close := strings.Index(trimmed, "]"); close > 1 {
			explicit = true
			numberText, rest = strings.TrimSpace(trimmed[1:close]), strings.TrimSpace(trimmed[close+1:])
		}
	default:
		numberText, rest = takeNumber(trimmed)
	}
	if numberText == "" {
		return Record{}, text, explicit, nil
	}
	number, parseErr := strconv.Atoi(numberText)
	if parseErr != nil || number < 1 {
		if explicit {
			return Record{}, text, true, fmt.Errorf("无效的聊天编号 #%s", numberText)
		}
		return Record{}, text, false, nil
	}
	value, ok := r.ByNumber(number)
	if !ok {
		if explicit {
			return Record{}, rest, true, fmt.Errorf("聊天编号 #%d 不存在。发送 /threads 查看可用编号。", number)
		}
		return Record{}, text, false, nil
	}
	if strings.TrimSpace(rest) == "" {
		return value, "", true, fmt.Errorf("请在 #%d 后输入消息或命令。", number)
	}
	return value, strings.TrimSpace(rest), true, nil
}

func takeNumber(value string) (string, string) {
	value = strings.TrimLeft(value, " \t")
	index := 0
	for index < len(value) && value[index] >= '0' && value[index] <= '9' {
		index++
	}
	if index == 0 {
		return "", ""
	}
	if index < len(value) && value[index] != ' ' && value[index] != '\t' && value[index] != '\r' && value[index] != '\n' {
		return "", ""
	}
	return value[:index], strings.TrimSpace(value[index:])
}

func (r *Registry) load() error {
	data, err := os.ReadFile(r.path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return fmt.Errorf("read thread numbers: %w", err)
	}
	var model diskModel
	if err := json.Unmarshal(data, &model); err != nil {
		return fmt.Errorf("decode thread numbers: %w", err)
	}
	if model.Version != 1 {
		return fmt.Errorf("decode thread numbers: unsupported version %d", model.Version)
	}
	max := 0
	for _, record := range model.Threads {
		if strings.TrimSpace(record.ThreadID) == "" || record.Number < 1 {
			return errors.New("decode thread numbers: invalid record")
		}
		if _, exists := r.byThread[record.ThreadID]; exists {
			return fmt.Errorf("decode thread numbers: duplicate thread %q", record.ThreadID)
		}
		if _, exists := r.byNumber[record.Number]; exists {
			return fmt.Errorf("decode thread numbers: duplicate number %d", record.Number)
		}
		r.byThread[record.ThreadID], r.byNumber[record.Number] = record, record.ThreadID
		if record.Number > max {
			max = record.Number
		}
	}
	r.nextNumber = model.NextNumber
	if r.nextNumber <= max {
		r.nextNumber = max + 1
	}
	if r.nextNumber < 1 {
		r.nextNumber = 1
	}
	return nil
}

func (r *Registry) saveLocked() error {
	if err := os.MkdirAll(filepath.Dir(r.path), 0o700); err != nil {
		return err
	}
	model := diskModel{Version: 1, NextNumber: r.nextNumber, Threads: make([]Record, 0, len(r.byThread))}
	for _, record := range r.byThread {
		model.Threads = append(model.Threads, record)
	}
	sort.Slice(model.Threads, func(i, j int) bool { return model.Threads[i].Number < model.Threads[j].Number })
	data, err := json.MarshalIndent(model, "", "  ")
	if err != nil {
		return err
	}
	temporary := r.path + ".tmp"
	if err := os.WriteFile(temporary, append(data, '\n'), 0o600); err != nil {
		return err
	}
	backup := r.path + ".bak"
	_ = os.Remove(backup)
	if _, err := os.Stat(r.path); err == nil {
		if err := os.Rename(r.path, backup); err != nil {
			_ = os.Remove(temporary)
			return err
		}
	}
	if err := os.Rename(temporary, r.path); err != nil {
		_ = os.Rename(backup, r.path)
		_ = os.Remove(temporary)
		return err
	}
	_ = os.Remove(backup)
	return nil
}

func parseTime(value string) time.Time {
	parsed, _ := time.Parse(time.RFC3339Nano, strings.TrimSpace(value))
	return parsed
}
func first(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}
