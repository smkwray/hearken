package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestTelemetryHelperProcess(t *testing.T) {
	if os.Getenv("HEARKEN_TEST_CHILD") != "1" {
		return
	}
	fmt.Println("child stdout")
	fmt.Fprintln(os.Stderr, "child stderr")
}

func TestRunChildWithTelemetryLabelsBothStreams(t *testing.T) {
	a := &App{bridgeStart: time.Now()}
	cmd := exec.Command(os.Args[0], "-test.run=TestTelemetryHelperProcess")
	cmd.Env = append(os.Environ(), "HEARKEN_TEST_CHILD=1")
	logPath := filepath.Join(t.TempDir(), "hearken.log")
	if err := a.runChildWithTelemetry(cmd, logPath, roleClientPlayDial, 3, ""); err != nil {
		t.Fatalf("run child: %v", err)
	}
	b, err := os.ReadFile(logPath)
	if err != nil {
		t.Fatal(err)
	}
	log := string(b)
	for _, want := range []string{
		"role=client-play-dial", "generation=3", "source=stdout", "msg=\"child stdout\"",
		"source=stderr", "msg=\"child stderr\"", "source=supervisor", "event=child_started",
	} {
		if !strings.Contains(log, want) {
			t.Errorf("telemetry log missing %q:\n%s", want, log)
		}
	}
}

func childLogSeqs(t *testing.T, path string) []int {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var seqs []int
	for _, ln := range strings.Split(string(b), "\n") {
		if i := strings.Index(ln, "seq="); i >= 0 {
			n, err := strconv.Atoi(strings.TrimRight(ln[i+4:], "\""))
			if err != nil {
				t.Fatalf("bad line %q", ln)
			}
			seqs = append(seqs, n)
		}
	}
	return seqs
}

// Both legs write telemetry at once. They must share one handle: on Windows a
// second handle makes the rotation rename fail, and the log grows without bound.
// Rotation must also keep the log bounded and the kept history contiguous.
func TestChildLogRotationKeepsHistoryWithTwoLegs(t *testing.T) {
	old := maxLogBytes
	maxLogBytes = 2048
	t.Cleanup(func() { maxLogBytes = old })

	path := filepath.Join(t.TempDir(), "hearken.log")
	a := &App{bridgeStart: time.Now()}
	for i := 0; i < 2; i++ {
		if err := a.openChildLog(path); err != nil {
			t.Fatal(err)
		}
	}
	if a.childLog.refs != 2 {
		t.Fatalf("two legs hold %d refs on the shared handle, want 2", a.childLog.refs)
	}

	const n = 400
	pad := strings.Repeat("x", 40)
	for i := 1; i <= n; i++ {
		r := roleClientPlayDial
		if i%2 == 0 {
			r = roleHostCapServe
		}
		a.writeChildLine(a.bridgeStart, r, 1, i, "s", "stderr", fmt.Sprintf("%s seq=%d", pad, i))
	}

	for _, p := range []string{path, path + ".old"} {
		fi, err := os.Stat(p)
		if err != nil {
			t.Fatalf("%s: %v", p, err)
		}
		if fi.Size() > maxLogBytes+512 {
			t.Fatalf("%s is %d bytes, cap %d: rotation did not bound it", p, fi.Size(), maxLogBytes)
		}
	}
	older, newer := childLogSeqs(t, path+".old"), childLogSeqs(t, path)
	all := append(older, newer...)
	if len(all) == 0 || all[len(all)-1] != n {
		t.Fatalf("latest line missing; kept %v", all)
	}
	for i := 1; i < len(all); i++ {
		if all[i] != all[i-1]+1 {
			t.Fatalf("history lost or split across files at seq %d -> %d", all[i-1], all[i])
		}
	}
	if fi, _ := os.Stat(path + ".old"); fi.Size() < maxLogBytes {
		t.Fatalf(".old holds %d bytes, want a full rotated segment (>= %d)", fi.Size(), maxLogBytes)
	}

	// One leg exits: the other keeps writing; the handle closes with the last leg.
	a.closeChildLog()
	a.writeChildLine(a.bridgeStart, roleHostCapServe, 1, n+1, "s", "stderr", fmt.Sprintf("seq=%d", n+1))
	if got := childLogSeqs(t, path); len(got) == 0 || got[len(got)-1] != n+1 {
		t.Fatalf("surviving leg's line missing after sibling exit: %v", got)
	}
	a.closeChildLog()
	if a.childLog.f != nil || a.childLog.refs != 0 {
		t.Fatalf("handle still open after both legs exited: refs=%d", a.childLog.refs)
	}
}

// logf rotates hearken.log by path while children hold the shared handle. The
// next child line must go to the new hearken.log, not keep growing .old.
func TestChildLogReopensAfterExternalRotate(t *testing.T) {
	path := filepath.Join(t.TempDir(), "hearken.log")
	a := &App{bridgeStart: time.Now()}
	if err := a.openChildLog(path); err != nil {
		t.Fatal(err)
	}
	defer a.closeChildLog()
	a.writeChildLine(a.bridgeStart, roleClientPlayDial, 1, 1, "s", "stderr", "before")
	if err := os.Rename(path, path+".old"); err != nil {
		t.Fatal(err)
	}
	a.writeChildLine(a.bridgeStart, roleClientPlayDial, 1, 1, "s", "stderr", "after")
	ob, _ := os.ReadFile(path + ".old")
	nb, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(ob), "after") || !strings.Contains(string(nb), "after") {
		t.Fatalf("post-rotate line went to .old:\n.old=%s\nlog=%s", ob, nb)
	}
}

func TestPathTypeLocalClassification(t *testing.T) {
	for peer, want := range map[string]string{
		"10.0.0.181":  "lan",
		"192.168.1.4": "lan",
		"not-an-ip":   "unknown",
		"8.8.8.8":     "unknown",
	} {
		if got := pathType(peer); got != want {
			t.Errorf("pathType(%q) = %q, want %q", peer, got, want)
		}
	}
}
