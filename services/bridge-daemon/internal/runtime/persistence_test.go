package runtime

import (
	"errors"
	"testing"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/control"
)

func TestSelectedThreadIdentityFailsClosed(t *testing.T) {
	tests := []struct {
		name     string
		selected string
		snapshot control.ThreadPersistenceSnapshot
		want     string
	}{
		{name: "same", selected: "thread-a", snapshot: control.ThreadPersistenceSnapshot{ThreadID: "thread-a"}, want: ""},
		{name: "missing", selected: "thread-a", snapshot: control.ThreadPersistenceSnapshot{}, want: StateThreadMismatch},
		{name: "different", selected: "thread-a", snapshot: control.ThreadPersistenceSnapshot{ThreadID: "thread-b"}, want: StateThreadMismatch},
		{name: "ephemeral", selected: "thread-a", snapshot: control.ThreadPersistenceSnapshot{ThreadID: "thread-a", Ephemeral: true}, want: StateFailed},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, got := selectedThreadIdentity(test.selected, test.snapshot)
			if got != test.want {
				t.Fatalf("selectedThreadIdentity state = %q; want %q", got, test.want)
			}
		})
	}
}

func TestCompletedNotificationRemainsUnverified(t *testing.T) {
	if got := completedNotificationState("completed"); got != StateCompletedUnverified {
		t.Fatalf("completed notification state = %q; want %q", got, StateCompletedUnverified)
	}
	if completedNotificationState("completed") == StatePersisted {
		t.Fatal("turn/completed alone must never be persisted")
	}
}

func TestEvaluatePersistenceRequiresIndependentProbeTurn(t *testing.T) {
	main := control.ThreadPersistenceSnapshot{ThreadID: "thread-a", FoundTurn: true, LastTurnID: "turn-a", TurnStatus: "completed", AssistantMessageItemID: "assistant-a"}
	probe := control.ThreadPersistenceSnapshot{ThreadID: "thread-a", FoundTurn: true, LastTurnID: "turn-a", TurnStatus: "completed", AssistantMessageItemID: "assistant-a"}
	status, _ := evaluatePersistence("thread-a", "turn-a", main, probe, nil, false)
	if status != StatePersisted {
		t.Fatalf("independent read result = %q; want %q", status, StatePersisted)
	}

	probe.FoundTurn = false
	status, _ = evaluatePersistence("thread-a", "turn-a", main, probe, nil, false)
	if status != StatePersistenceFailed {
		t.Fatalf("missing probe turn result = %q; want %q", status, StatePersistenceFailed)
	}

	status, _ = evaluatePersistence("thread-a", "turn-a", main, control.ThreadPersistenceSnapshot{}, errors.New("probe unavailable"), false)
	if status != StatePersistenceFailed {
		t.Fatalf("failed probe result = %q; want %q", status, StatePersistenceFailed)
	}

	main.TurnStatus = "inProgress"
	probe.TurnStatus = "inProgress"
	probe.FoundTurn = true
	status, _ = evaluatePersistence("thread-a", "turn-a", main, probe, nil, false)
	if status != StatePersistenceFailed {
		t.Fatalf("running turn result = %q; want %q", status, StatePersistenceFailed)
	}
}

func TestEvaluatePersistenceRejectsThreadMismatchButTrustsIndependentEvidenceOverStderr(t *testing.T) {
	main := control.ThreadPersistenceSnapshot{ThreadID: "thread-b", FoundTurn: true, TurnStatus: "completed", AssistantMessageItemID: "assistant-a"}
	probe := control.ThreadPersistenceSnapshot{ThreadID: "thread-a", FoundTurn: true, TurnStatus: "completed", AssistantMessageItemID: "assistant-a"}
	status, _ := evaluatePersistence("thread-a", "turn-a", main, probe, nil, false)
	if status != StateThreadMismatch {
		t.Fatalf("thread mismatch result = %q; want %q", status, StateThreadMismatch)
	}

	main.ThreadID = "thread-a"
	status, _ = evaluatePersistence("thread-a", "turn-a", main, probe, nil, true)
	if status != StatePersisted {
		t.Fatalf("independently verified stderr warning result = %q; want %q", status, StatePersisted)
	}
}

func TestEvaluatePersistenceWaitsForAssistantMessage(t *testing.T) {
	main := control.ThreadPersistenceSnapshot{ThreadID: "thread-a", FoundTurn: true, TurnStatus: "completed"}
	probe := control.ThreadPersistenceSnapshot{ThreadID: "thread-a", FoundTurn: true, TurnStatus: "completed", AssistantMessageItemID: "assistant-a"}
	status, _ := evaluatePersistence("thread-a", "turn-a", main, probe, nil, false)
	if status != StatePersistenceFailed {
		t.Fatalf("missing main assistant result = %q; want %q", status, StatePersistenceFailed)
	}
	main.AssistantMessageItemID = "assistant-a"
	probe.AssistantMessageItemID = ""
	status, _ = evaluatePersistence("thread-a", "turn-a", main, probe, nil, false)
	if status != StatePersistenceFailed {
		t.Fatalf("missing probe assistant result = %q; want %q", status, StatePersistenceFailed)
	}
}

func TestTurnCompletedErrorFailsClosed(t *testing.T) {
	if !completionErrorPresent(map[string]any{"error": map[string]any{"message": "content is not inspected"}}) {
		t.Fatal("non-empty turn/completed.error must be detected")
	}
	if completionErrorPresent(map[string]any{"error": nil}) {
		t.Fatal("null turn/completed.error must not be treated as an error")
	}
}
