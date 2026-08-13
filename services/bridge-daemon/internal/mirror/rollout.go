package mirror

import (
	"bufio"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/fsnotify/fsnotify"
)

func (s *Service) watchRollouts() {
	root := strings.TrimSpace(os.Getenv("CODEX_HOME"))
	if root == "" {
		if profile := strings.TrimSpace(os.Getenv("USERPROFILE")); profile != "" {
			root = filepath.Join(profile, ".codex")
		}
	}
	sessions := filepath.Join(root, "sessions")
	w, err := fsnotify.NewWatcher()
	if err != nil {
		s.logger.Printf("rolloutWatcher result=start-failed error=%s", err)
		return
	}
	defer w.Close()
	addTree := func(path string) {
		_ = filepath.WalkDir(path, func(p string, d os.DirEntry, err error) error {
			if err == nil && d.IsDir() {
				_ = w.Add(p)
			}
			return nil
		})
	}
	addTree(sessions)
	s.logger.Printf("rolloutWatcher result=started root=%s", sessions)
	timers := map[string]*time.Timer{}
	known := map[string]fileStamp{}
	_ = filepath.WalkDir(sessions, func(path string, entry os.DirEntry, err error) error {
		if err == nil && !entry.IsDir() && isRollout(path) {
			if info, e := entry.Info(); e == nil {
				known[path] = fileStamp{info.Size(), info.ModTime()}
			}
		}
		return nil
	})
	reconcile := time.NewTicker(2 * time.Second)
	defer reconcile.Stop()
	for {
		select {
		case <-s.ctx.Done():
			return
		case <-reconcile.C:
			_ = filepath.WalkDir(sessions, func(path string, entry os.DirEntry, err error) error {
				if err != nil || entry.IsDir() || !isRollout(path) {
					return nil
				}
				info, e := entry.Info()
				if e != nil {
					return nil
				}
				stamp := fileStamp{info.Size(), info.ModTime()}
				if previous, ok := known[path]; !ok || previous != stamp {
					known[path] = stamp
					s.onRolloutChanged(path)
				}
				return nil
			})
		case err, ok := <-w.Errors:
			if !ok {
				return
			}
			if err != nil {
				s.logger.Printf("rolloutWatcher result=error error=%s", err)
			}
		case event, ok := <-w.Events:
			if !ok {
				return
			}
			if event.Op&fsnotify.Create != 0 {
				if info, e := os.Stat(event.Name); e == nil && info.IsDir() {
					addTree(event.Name)
					continue
				}
			}
			if !isRollout(event.Name) {
				continue
			}
			if timer := timers[event.Name]; timer != nil {
				timer.Stop()
			}
			path := event.Name
			timers[path] = time.AfterFunc(250*time.Millisecond, func() { s.onRolloutChanged(path) })
		}
	}
}

type fileStamp struct {
	Size    int64
	ModTime time.Time
}

func isRollout(path string) bool {
	name := strings.ToLower(filepath.Base(path))
	return strings.HasPrefix(name, "rollout-") && strings.HasSuffix(name, ".jsonl")
}

func (s *Service) onRolloutChanged(path string) {
	final, ok := readCompletedRolloutFinal(path)
	if !ok {
		return
	}
	s.mu.Lock()
	previous := s.rolloutFinals[final.ThreadID]
	if previous.TurnID == final.TurnID && previous.ItemID == final.ItemID {
		s.mu.Unlock()
		return
	}
	s.rolloutFinals[final.ThreadID] = final
	s.mu.Unlock()
	s.logFinalMilestone("turn_completed", final.ThreadID, final.TurnID, "watcher")
	s.logFinalMilestone("final_first_observed", final.ThreadID, final.TurnID, "watcher")
	s.triggerSync(final.ThreadID, "watcher", final.TurnID)
}

func readCompletedRolloutFinal(path string) (rolloutFinal, bool) {
	file, err := os.Open(path)
	if err != nil {
		return rolloutFinal{}, false
	}
	defer file.Close()
	var threadID, finalTurn string
	items := map[string]rolloutFinal{}
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 64*1024), 16*1024*1024)
	for scanner.Scan() {
		var row struct {
			Timestamp, Type string
			Payload         map[string]any
		}
		if json.Unmarshal(scanner.Bytes(), &row) != nil {
			continue
		}
		if row.Type == "session_meta" {
			threadID = stringField(row.Payload, "id")
		}
		if row.Type == "response_item" && stringField(row.Payload, "type") == "message" && stringField(row.Payload, "role") == "assistant" {
			phase := stringField(row.Payload, "phase")
			if phase == "commentary" {
				continue
			}
			turnID := nestedString(row.Payload, "internal_chat_message_metadata_passthrough", "turn_id")
			if turnID == "" {
				continue
			}
			text := contentText(row.Payload["content"])
			if text == "" {
				continue
			}
			id := stringField(row.Payload, "id")
			if id == "" {
				id = fingerprint(turnID, text)
			}
			items[turnID] = rolloutFinal{ThreadID: threadID, TurnID: turnID, ItemID: id, Text: text, CompletedAt: row.Timestamp}
		}
		if row.Type == "event_msg" && stringField(row.Payload, "type") == "task_complete" {
			finalTurn = stringField(row.Payload, "turn_id")
		}
	}
	item, ok := items[finalTurn]
	item.ThreadID = threadID
	return item, ok && threadID != "" && finalTurn != ""
}

func stringField(value map[string]any, key string) string {
	text, _ := value[key].(string)
	return strings.TrimSpace(text)
}
func nestedString(value map[string]any, object, key string) string {
	nested, _ := value[object].(map[string]any)
	return stringField(nested, key)
}
func contentText(value any) string {
	values, _ := value.([]any)
	parts := []string{}
	for _, raw := range values {
		item, _ := raw.(map[string]any)
		if stringField(item, "type") == "output_text" {
			if text := stringField(item, "text"); text != "" {
				parts = append(parts, text)
			}
		}
	}
	return strings.TrimSpace(strings.Join(parts, "\n"))
}

func containsTurn(messages []visibleMessage, turnID string) bool {
	for _, message := range messages {
		if message.TurnID == turnID {
			return true
		}
	}
	return false
}
