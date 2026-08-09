package control

import "testing"

func TestNormalizeThreadListProducesStableDTO(t *testing.T) {
	result := normalizeThreadList(map[string]any{
		"data": []any{map[string]any{
			"id":        "thread-1",
			"preview":   "First line\nSecond line",
			"cwd":       `C:\work\demo`,
			"model":     "gpt-5",
			"createdAt": float64(1_700_000_000),
			"updatedAt": float64(1_700_000_100),
			"status": map[string]any{
				"type":        "active",
				"activeFlags": []any{"waitingOnInput"},
			},
		}},
		"nextCursor": "next-page",
	})
	if len(result.Threads) != 1 {
		t.Fatalf("threads = %d, want 1", len(result.Threads))
	}
	thread := result.Threads[0]
	if thread.ThreadID != "thread-1" || thread.Title != "First line" {
		t.Fatalf("unexpected thread identity: %#v", thread)
	}
	if thread.Status != "active[waitingOnInput]" {
		t.Fatalf("status = %q", thread.Status)
	}
	if thread.Archived != nil {
		t.Fatalf("archived should be unknown, got %#v", thread.Archived)
	}
	if result.NextCursor != "next-page" {
		t.Fatalf("next cursor = %q", result.NextCursor)
	}
}

func TestNormalizeThreadDetailParsesMessagesAndTools(t *testing.T) {
	detail := normalizeThreadDetail(map[string]any{"thread": map[string]any{
		"id": "thread-1",
		"turns": []any{map[string]any{
			"id":     "turn-1",
			"status": "completed",
			"items": []any{
				map[string]any{"id": "user-1", "type": "userMessage", "content": []any{map[string]any{"type": "text", "text": "你好"}}},
				map[string]any{"id": "tool-1", "type": "commandExecution", "command": "git status", "status": "completed", "aggregatedOutput": "clean"},
			},
		}},
	}})
	if len(detail.Turns) != 1 || len(detail.Turns[0].Items) != 2 {
		t.Fatalf("unexpected detail shape: %#v", detail)
	}
	if detail.Turns[0].Items[0].Text != "你好" || detail.Turns[0].Items[1].Label != "git status" {
		t.Fatalf("unexpected normalized items: %#v", detail.Turns[0].Items)
	}
}
