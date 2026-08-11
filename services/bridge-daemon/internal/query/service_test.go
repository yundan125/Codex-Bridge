package query

import (
	"context"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/control"
	"cloudlight.dev/codexbridge/bridge-daemon/internal/interactions"
	bridgeruntime "cloudlight.dev/codexbridge/bridge-daemon/internal/runtime"
	"cloudlight.dev/codexbridge/bridge-daemon/internal/threadregistry"
)

type fakeControl struct {
	threads []control.ThreadSummary
	details map[string]control.ThreadDetail
}

func (f *fakeControl) ListThreads(_ context.Context, limit int, _ string) (control.ThreadList, error) {
	threads := append([]control.ThreadSummary(nil), f.threads...)
	if limit < len(threads) {
		threads = threads[:limit]
	}
	return control.ThreadList{Threads: threads}, nil
}

func (f *fakeControl) ReadThread(_ context.Context, threadID string, _ bool) (control.ThreadDetail, error) {
	return f.details[threadID], nil
}

type fakeRuntime struct {
	status       bridgeruntime.Status
	states       map[string]control.RuntimeState
	interactions []interactions.PendingInteraction
	rateLimits   map[string]any
}

func (f *fakeRuntime) Status() bridgeruntime.Status { return f.status }
func (f *fakeRuntime) RuntimeState(threadID string) control.RuntimeState {
	return f.states[threadID]
}
func (f *fakeRuntime) ListInteractions(string) []interactions.PendingInteraction {
	return append([]interactions.PendingInteraction(nil), f.interactions...)
}
func (f *fakeRuntime) AccountRateLimits(context.Context) (map[string]any, error) {
	return f.rateLimits, nil
}

func newRegistry(t *testing.T, threads []control.ThreadSummary) *threadregistry.Registry {
	t.Helper()
	registry, err := threadregistry.New(filepath.Join(t.TempDir(), "thread-numbers.json"))
	if err != nil {
		t.Fatal(err)
	}
	metadata := make([]threadregistry.Metadata, 0, len(threads))
	for _, thread := range threads {
		metadata = append(metadata, threadregistry.Metadata{ThreadID: thread.ThreadID, Title: thread.Title, CreatedAt: thread.CreatedAt, LastSeenAt: thread.UpdatedAt})
	}
	if _, err := registry.EnsureBatch(metadata); err != nil {
		t.Fatal(err)
	}
	return registry
}

func TestHistoryReturnsLastCompletedUserAssistantRounds(t *testing.T) {
	threads := []control.ThreadSummary{{ThreadID: "thread-1", Title: "历史测试", CreatedAt: "2026-01-01T00:00:00Z", UpdatedAt: "2026-08-11T09:00:00Z"}}
	registry := newRegistry(t, threads)
	record, _ := registry.ByThreadID("thread-1")
	turns := []control.Turn{
		{Status: "completed", Items: []control.Item{{Type: "userMessage", Text: "第一问"}, {Type: "agentMessage", Phase: "commentary", Text: "中间进度"}, {Type: "commandExecution", Output: "secret stdout"}, {Type: "agentMessage", Phase: "final_answer", Text: "第一答"}}},
		{Status: "completed", Items: []control.Item{{Type: "userMessage", Text: "第二问"}, {Type: "agentMessage", Phase: "final_answer", Text: "第二答"}}},
		{Status: "inProgress", Items: []control.Item{{Type: "userMessage", Text: "第三问"}, {Type: "agentMessage", Phase: "commentary", Text: "尚在执行"}}},
	}
	controlService := &fakeControl{threads: threads, details: map[string]control.ThreadDetail{"thread-1": {ThreadSummary: threads[0], Turns: turns}}}
	service := New(controlService, &fakeRuntime{states: map[string]control.RuntimeState{}}, registry)
	result, handled := service.Execute(context.Background(), "/history #"+itoa(record.Number)+" 2")
	joined := strings.Join(result.Parts, "\n")
	if !handled || !strings.Contains(joined, "第二问") || !strings.Contains(joined, "第二答") || !strings.Contains(joined, "第三问") || !strings.Contains(joined, "尚未完成") {
		t.Fatalf("unexpected history: %s", joined)
	}
	for _, excluded := range []string{"第一问", "第一答", "中间进度", "尚在执行", "secret stdout"} {
		if strings.Contains(joined, excluded) {
			t.Fatalf("history leaked excluded content %q: %s", excluded, joined)
		}
	}
	info, _ := service.Execute(context.Background(), "/thread ["+itoa(record.Number)+"]")
	if !strings.Contains(info.Parts[0], "#"+itoa(record.Number)+" 历史测试") {
		t.Fatalf("bracket selector failed: %s", info.Parts[0])
	}
	missing, _ := service.Execute(context.Background(), "/history #999 3")
	if missing.Parts[0] != "聊天编号 #999 不存在。" {
		t.Fatalf("missing selector message = %q", missing.Parts[0])
	}
}

func TestRunningWaitingRecentAndFailedUseReadOnlyRuntimeTruth(t *testing.T) {
	now := time.Date(2026, 8, 11, 18, 0, 0, 0, time.Local)
	threads := []control.ThreadSummary{
		{ThreadID: "running", Number: 1, Title: "正在运行", UpdatedAt: now.Add(-2 * time.Minute).UTC().Format(time.RFC3339)},
		{ThreadID: "input", Number: 2, Title: "等待输入", UpdatedAt: now.Add(-18 * time.Minute).UTC().Format(time.RFC3339)},
		{ThreadID: "approval", Number: 3, Title: "等待审批", UpdatedAt: now.Add(-time.Hour).UTC().Format(time.RFC3339)},
		{ThreadID: "failed", Number: 4, Title: "构建任务", UpdatedAt: now.Add(-2 * time.Hour).UTC().Format(time.RFC3339)},
		{ThreadID: "idle", Number: 5, Title: "空闲", Status: "running", UpdatedAt: now.Add(-3 * time.Hour).UTC().Format(time.RFC3339)},
	}
	details := map[string]control.ThreadDetail{}
	for _, thread := range threads {
		details[thread.ThreadID] = control.ThreadDetail{ThreadSummary: thread}
	}
	failed := details["failed"]
	failed.Turns = []control.Turn{{Status: "failed", Error: "compile command exited", UpdatedAt: now.Add(-2 * time.Hour).UTC().Format(time.RFC3339)}}
	details["failed"] = failed
	runtime := &fakeRuntime{
		states: map[string]control.RuntimeState{
			"running":  {State: bridgeruntime.StateRunningExternal, StartedAt: now.Add(-2 * time.Minute).UTC().Format(time.RFC3339)},
			"input":    {State: bridgeruntime.StateWaitingUserInput},
			"approval": {State: bridgeruntime.StateWaitingApproval},
			"failed":   {State: bridgeruntime.StateFailed},
			"idle":     {State: bridgeruntime.StateIdle},
		},
		interactions: []interactions.PendingInteraction{
			{ThreadID: "input", Kind: interactions.KindUserInput, Status: "pending"},
			{ThreadID: "approval", Kind: interactions.KindCommandApproval, Status: "pending"},
		},
	}
	service := New(&fakeControl{threads: threads, details: details}, runtime, newRegistry(t, threads))
	service.now = func() time.Time { return now }
	running, _ := service.Execute(context.Background(), "/running")
	if !strings.Contains(running.Parts[0], "#1 正在运行") || strings.Contains(running.Parts[0], "#5") || strings.Contains(running.Parts[0], "等待输入") {
		t.Fatalf("running result: %s", running.Parts[0])
	}
	waiting, _ := service.Execute(context.Background(), "/waiting")
	if !strings.Contains(waiting.Parts[0], "#2 等待输入 · 等待你的回答") || !strings.Contains(waiting.Parts[0], "#3 等待审批 · 等待桌面端审批") || strings.Contains(waiting.Parts[0], "#1") {
		t.Fatalf("waiting result: %s", waiting.Parts[0])
	}
	recent, _ := service.Execute(context.Background(), "/recent")
	if !strings.Contains(recent.Parts[0], "#1 正在运行 · 2 分钟前") || strings.Index(recent.Parts[0], "#1") > strings.Index(recent.Parts[0], "#2") {
		t.Fatalf("recent result: %s", recent.Parts[0])
	}
	failures, _ := service.Execute(context.Background(), "/failed")
	if !strings.Contains(failures.Parts[0], "#4 构建任务") || !strings.Contains(failures.Parts[0], "原因：构建失败") {
		t.Fatalf("failed result: %s", failures.Parts[0])
	}
}

func TestQuotaUsesOfficialRateLimitSnapshotWithoutInference(t *testing.T) {
	reset := time.Date(2026, 8, 11, 20, 30, 0, 0, time.Local)
	runtime := &fakeRuntime{states: map[string]control.RuntimeState{}, rateLimits: map[string]any{
		"rateLimitsByLimitId": map[string]any{"codex": map[string]any{
			"planType":  "plus",
			"primary":   map[string]any{"usedPercent": float64(28), "windowDurationMins": float64(300), "resetsAt": float64(reset.Unix())},
			"secondary": map[string]any{"usedPercent": float64(57), "windowDurationMins": float64(10080), "resetsAt": float64(reset.Add(7 * 24 * time.Hour).Unix())},
		}},
	}}
	service := New(&fakeControl{details: map[string]control.ThreadDetail{}}, runtime, nil)
	service.now = func() time.Time { return time.Date(2026, 8, 11, 18, 0, 0, 0, time.Local) }
	result, _ := service.Execute(context.Background(), "/quota")
	text := result.Parts[0]
	for _, expected := range []string{"套餐：Plus", "5 小时额度：剩余 72%", "周额度：剩余 43%", "恢复时间：今天 20:30"} {
		if !strings.Contains(text, expected) {
			t.Fatalf("quota missing %q: %s", expected, text)
		}
	}
}

func itoa(value int) string { return strconv.Itoa(value) }
