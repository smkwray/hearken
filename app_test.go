package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
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
