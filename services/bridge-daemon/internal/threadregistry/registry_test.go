package threadregistry

import (
	"path/filepath"
	"testing"
)

func TestRegistryAssignsOldestFirstAndPersistsWithoutReuse(t *testing.T) {
	path := filepath.Join(t.TempDir(), "thread-numbers.json")
	r, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	_, err = r.EnsureBatch([]Metadata{{ThreadID: "new", CreatedAt: "2026-02-01T00:00:00Z"}, {ThreadID: "old", CreatedAt: "2026-01-01T00:00:00Z"}})
	if err != nil {
		t.Fatal(err)
	}
	if old, _ := r.ByThreadID("old"); old.Number != 1 {
		t.Fatalf("old number=%d", old.Number)
	}
	if newer, _ := r.ByThreadID("new"); newer.Number != 2 {
		t.Fatalf("new number=%d", newer.Number)
	}
	reloaded, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	added, err := reloaded.Ensure(Metadata{ThreadID: "next"})
	if err != nil {
		t.Fatal(err)
	}
	if added.Number != 3 {
		t.Fatalf("next number=%d", added.Number)
	}
}

func TestParsePrefixPriorityAndSafeBareNumber(t *testing.T) {
	r, _ := New(filepath.Join(t.TempDir(), "registry.json"))
	_, _ = r.Ensure(Metadata{ThreadID: "thread", Title: "Title"})
	for _, input := range []string{"#1 hello", "[1] hello", "1 hello"} {
		record, content, recognized, err := r.ParsePrefix(input)
		if err != nil || !recognized || record.ThreadID != "thread" || content != "hello" {
			t.Fatalf("parse %q: %#v %q %t %v", input, record, content, recognized, err)
		}
	}
	if _, _, recognized, _ := r.ParsePrefix("12 apples"); recognized {
		t.Fatal("unknown bare number was routed")
	}
}
