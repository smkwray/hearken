package main

import (
	"context"
	"os"
	"path/filepath"
	"slices"
	"testing"
)

func TestReceiveOnlyHostUsesDefaultOutputHelper(t *testing.T) {
	tmp := t.TempDir()
	t.Setenv("HOME", tmp)
	t.Setenv("HEARKEN_PLAY_BIND_IP", "192.0.2.1")
	app := NewApp()
	cfg := defaultConfig()
	cfg.Direction = "win2mac" // Also exercise configs saved before role-based names.
	if deps := checkDeps(cfg); !slices.Equal(deps, []string{"hear-play"}) {
		t.Fatalf("missing dependencies: %v", deps)
	}
	player := filepath.Join(tmp, "audio-bridge", "hear-play")
	if err := os.MkdirAll(filepath.Dir(player), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(player, nil, 0700); err != nil {
		t.Fatal(err)
	}
	for _, dir := range []string{"win2mac", "clientToHost"} {
		cfg.Direction = dir
		roles := rolesForDirection(dir, isHost(cfg))
		if !slices.Equal(roles, []role{roleHostPlayServe}) || len(checkDeps(cfg)) != 0 {
			t.Fatalf("%s: roles=%v deps=%v", dir, roles, checkDeps(cfg))
		}
		cmd, retry := app.buildCmd(context.Background(), roles[0], cfg)
		want := []string{player, "192.0.2.1", "45001", "8000", "1.000"}
		if !retry || cmd == nil || !slices.Equal(cmd.Args, want) {
			t.Fatalf("%s: command=%v retry=%t", dir, cmd, retry)
		}
	}
	cfg.Direction = "both"
	if receiveOnlyMacHost(cfg) {
		t.Fatal("duplex playback must avoid the capture aggregate")
	}
}
