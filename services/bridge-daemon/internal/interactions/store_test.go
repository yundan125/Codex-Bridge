package interactions

import (
	"testing"
	"time"
)

func TestInteractionCanBeginResponseOnlyOnce(t *testing.T) {
	store := NewStore()
	item := store.Add("item/commandExecution/requestApproval", "n:42", map[string]any{
		"threadId": "thread-1", "turnId": "turn-1", "command": "echo safe",
	}, time.Now())
	if _, err := store.BeginResponse(item.ID); err != nil {
		t.Fatalf("first response should begin: %v", err)
	}
	if _, err := store.BeginResponse(item.ID); err == nil {
		t.Fatal("second response must conflict")
	}
	store.Complete(item.ID, "allowed")
	if _, err := store.BeginResponse(item.ID); err == nil {
		t.Fatal("completed interaction must stay single-use")
	}
}

func TestResolveNumericServerRequestID(t *testing.T) {
	store := NewStore()
	item := store.Add("item/tool/requestUserInput", "n:9", map[string]any{"threadId": "thread-1"}, time.Now())
	resolved, ok := store.ResolveByRequest("9")
	if !ok || resolved.ID != item.ID || resolved.Status != "resolved" {
		t.Fatalf("numeric request id was not resolved: %#v, %v", resolved, ok)
	}
}
