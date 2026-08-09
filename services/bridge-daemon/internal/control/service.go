package control

import (
	"context"
	"errors"
	"time"

	"cloudlight.dev/codexbridge/bridge-daemon/internal/threadregistry"
)

var ErrUnavailable = errors.New("Codex app-server is unavailable")

type ThreadReader interface {
	ThreadList(ctx context.Context, limit int, cursor string) (map[string]any, error)
	ThreadRead(ctx context.Context, threadID string, includeTurns bool) (map[string]any, error)
}

type RuntimeStateProvider interface {
	RuntimeState(threadID string) RuntimeState
}

type Service struct {
	reader   ThreadReader
	states   RuntimeStateProvider
	registry *threadregistry.Registry
}

func NewService(reader ThreadReader, states RuntimeStateProvider, registry *threadregistry.Registry) *Service {
	return &Service{reader: reader, states: states, registry: registry}
}

func (s *Service) ListThreads(ctx context.Context, limit int, cursor string) (ThreadList, error) {
	raw, err := s.reader.ThreadList(ctx, limit, cursor)
	if err != nil {
		return ThreadList{}, err
	}
	result := normalizeThreadList(raw)
	if s.registry != nil {
		metadata := make([]threadregistry.Metadata, 0, len(result.Threads))
		for _, thread := range result.Threads {
			metadata = append(metadata, threadMetadata(thread))
		}
		_, _ = s.registry.EnsureBatch(metadata)
	}
	if s.states != nil {
		for index := range result.Threads {
			if s.registry != nil {
				if record, ok := s.registry.ByThreadID(result.Threads[index].ThreadID); ok {
					result.Threads[index].Number = record.Number
				}
			}
			result.Threads[index].Status = s.states.RuntimeState(result.Threads[index].ThreadID).State
		}
	}
	return result, nil
}

func (s *Service) ReadThread(ctx context.Context, threadID string, includeTurns bool) (ThreadDetail, error) {
	raw, err := s.reader.ThreadRead(ctx, threadID, includeTurns)
	if err != nil {
		return ThreadDetail{}, err
	}
	detail := normalizeThreadDetail(raw)
	if s.registry != nil && detail.ThreadID != "" {
		record, _ := s.registry.Ensure(threadMetadata(detail.ThreadSummary))
		detail.Number = record.Number
	}
	if s.states != nil {
		detail.Runtime = s.states.RuntimeState(threadID)
	}
	if detail.Archived != nil && *detail.Archived {
		detail.Runtime.CanSend = false
	}
	return detail, nil
}

func threadMetadata(thread ThreadSummary) threadregistry.Metadata {
	return threadregistry.Metadata{ThreadID: thread.ThreadID, Title: thread.Title, CWD: thread.CWD, CreatedAt: thread.CreatedAt, LastSeenAt: firstSeen(thread.UpdatedAt)}
}

func firstSeen(value string) string {
	if value != "" {
		return value
	}
	return time.Now().UTC().Format(time.RFC3339Nano)
}
