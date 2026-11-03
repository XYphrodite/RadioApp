package tsnet

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"tailscale.com/envknob"
	"tailscale.com/tsnet"
)

func init() {
	// Keep WireGuard traffic on HTTPS DERP (TCP 443) with a conservative MTU so
	// userspace tsnet remains stable when Android routes traffic through an outer
	// system VPN/proxy (e.g. Happ / VLESS / Xray TUN) where UDP paths blackhole full frames.
	_ = os.Setenv("TS_DEBUG_ALWAYS_USE_DERP", "true")
	_ = os.Setenv("TS_DEBUG_MTU", "1200")
	envknob.Setenv("TS_DEBUG_ALWAYS_USE_DERP", "true")
	envknob.Setenv("TS_DEBUG_MTU", "1200")
}

// Tsnet is the gomobile binding exported to Java as tsnet.Tsnet_.
// Runs completely in userspace without Android VpnService so Happ/system VPNs remain active.
type Tsnet struct {
	mu          sync.Mutex
	srv         *tsnet.Server
	cancel      context.CancelFunc
	fwdLn       net.Listener
	fwdTarget   string
	fwdURL      string
	peerIPCache map[string]string
}

func configureAndroidEnv(stateDir string) {
	filesDir := filepath.Dir(stateDir)
	if filesDir != "" && filesDir != "." && filesDir != "/" {
		_ = os.Setenv("HOME", filesDir)
		_ = os.Setenv("TMPDIR", filesDir)
		_ = os.Setenv("XDG_CACHE_HOME", filepath.Join(filesDir, "cache"))
		_ = os.Setenv("XDG_CONFIG_HOME", filesDir)
		_ = os.Setenv("XDG_DATA_HOME", filepath.Join(filesDir, "data"))
		_ = os.MkdirAll(filepath.Join(filesDir, "cache"), 0700)
		_ = os.MkdirAll(filepath.Join(filesDir, "data"), 0700)
	}
	envknob.Setenv("TS_DEBUG_ALWAYS_USE_DERP", "true")
	envknob.Setenv("TS_DEBUG_MTU", "1200")
}

// Account is the non-blocking interactive browser-login API for mobile UI.
func (t *Tsnet) Account(command, payload string) (string, error) {
	if command == "begin" {
		var args struct {
			Hostname string
			StateDir string
		}
		if err := json.Unmarshal([]byte(payload), &args); err != nil {
			return "", err
		}
		if args.StateDir == "" {
			return "", fmt.Errorf("state directory required")
		}
		if err := os.MkdirAll(args.StateDir, 0700); err != nil {
			return "", err
		}
		t.mu.Lock()
		if t.srv == nil {
			configureAndroidEnv(args.StateDir)
			srv := &tsnet.Server{
				Dir:       args.StateDir,
				Hostname:  args.Hostname,
				Ephemeral: false,
				Logf:      func(string, ...any) {},
			}
			if err := srv.Start(); err != nil {
				t.mu.Unlock()
				return "", err
			}
			t.srv = srv
		}
		t.mu.Unlock()
	}

	t.mu.Lock()
	srv := t.srv
	t.mu.Unlock()
	if srv == nil {
		return "", fmt.Errorf("account not started")
	}

	lc, err := srv.LocalClient()
	if err != nil {
		return "", err
	}

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	if command == "logout" {
		_ = lc.Logout(ctx)
		return "{}", t.Stop()
	}

	status, err := lc.Status(ctx)
	if err != nil {
		return "", err
	}

	if command == "begin" && status.BackendState == "NeedsLogin" && status.AuthURL == "" {
		if err := lc.StartLoginInteractive(ctx); err == nil {
			time.Sleep(250 * time.Millisecond)
			if updated, err2 := lc.Status(ctx); err2 == nil {
				status = updated
			}
		}
	}

	type peer struct {
		ID     string `json:"id"`
		Name   string `json:"name"`
		DNS    string `json:"dns"`
		IP     string `json:"ip"`
		Online bool   `json:"online"`
	}
	peers := []peer{}
	cacheUpdates := map[string]string{}
	for _, p := range status.Peer {
		for _, addr := range p.TailscaleIPs {
			if addr.Is4() {
				ipStr := addr.String()
				cleanDNS := strings.TrimSuffix(p.DNSName, ".")
				peers = append(peers, peer{
					ID:     string(p.ID),
					Name:   p.HostName,
					DNS:    cleanDNS,
					IP:     ipStr,
					Online: p.Online,
				})
				if p.HostName != "" {
					cacheUpdates[strings.ToLower(p.HostName)] = ipStr
				}
				if cleanDNS != "" {
					lowerDNS := strings.ToLower(cleanDNS)
					cacheUpdates[lowerDNS] = ipStr
					if dot := strings.IndexByte(lowerDNS, '.'); dot > 0 {
						cacheUpdates[lowerDNS[:dot]] = ipStr
					}
				}
				break
			}
		}
	}

	if len(cacheUpdates) > 0 {
		t.mu.Lock()
		if t.peerIPCache == nil {
			t.peerIPCache = make(map[string]string, len(cacheUpdates))
		}
		for k, v := range cacheUpdates {
			t.peerIPCache[k] = v
		}
		t.mu.Unlock()
	}

	login := ""
	if status.Self != nil {
		if u, ok := status.User[status.Self.UserID]; ok {
			login = u.LoginName
		}
	}

	result, err := json.Marshal(struct {
		Running bool   `json:"running"`
		State   string `json:"state"`
		AuthURL string `json:"authUrl"`
		Login   string `json:"login"`
		Peers   []peer `json:"peers"`
	}{
		Running: status.BackendState == "Running",
		State:   status.BackendState,
		AuthURL: status.AuthURL,
		Login:   login,
		Peers:   peers,
	})
	return string(result), err
}

// Start launches tsnet using an AuthKey (ephemeral/reusable).
func (t *Tsnet) Start(authKey, hostname, controlURL, stateDir string, logVerbosity int) error {
	t.mu.Lock()
	if t.srv != nil {
		t.mu.Unlock()
		return nil
	}
	if stateDir == "" {
		stateDir = filepath.Join(os.TempDir(), "tsnet-radiowork")
	}
	if err := os.MkdirAll(stateDir, 0700); err != nil {
		t.mu.Unlock()
		return fmt.Errorf("mkdir stateDir: %w", err)
	}
	configureAndroidEnv(stateDir)

	srv := &tsnet.Server{
		Dir:        stateDir,
		Hostname:   hostname,
		AuthKey:    authKey,
		ControlURL: controlURL,
		Ephemeral:  true,
		Logf:       func(string, ...any) {},
	}
	if logVerbosity > 0 {
		srv.Logf = func(f string, a ...any) { fmt.Printf(f+"\n", a...) }
	}
	_, cancel := context.WithCancel(context.Background())
	t.srv = srv
	t.cancel = cancel
	t.mu.Unlock()

	ctx, upCancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer upCancel()
	if _, err := srv.Up(ctx); err != nil {
		t.mu.Lock()
		if t.srv == srv {
			t.srv = nil
			t.cancel = nil
		}
		t.mu.Unlock()
		_ = srv.Close()
		return fmt.Errorf("tsnet up: %w", err)
	}
	return nil
}

// StopForwarder closes the local HTTP/TCP loopback listener if running.
func (t *Tsnet) StopForwarder() {
	t.mu.Lock()
	ln := t.fwdLn
	t.fwdLn = nil
	t.fwdTarget = ""
	t.fwdURL = ""
	t.mu.Unlock()
	if ln != nil {
		_ = ln.Close()
	}
}

func (t *Tsnet) Stop() error {
	t.StopForwarder()

	t.mu.Lock()
	defer t.mu.Unlock()
	if t.srv == nil {
		return nil
	}
	if t.cancel != nil {
		t.cancel()
	}
	err := t.srv.Close()
	t.srv = nil
	t.cancel = nil
	t.peerIPCache = nil
	return err
}

func (t *Tsnet) resolveTailnetHost(srv *tsnet.Server, host string) string {
	clean := strings.TrimSpace(host)
	if clean == "" {
		return clean
	}
	if _, err := netip.ParseAddr(clean); err == nil {
		return clean
	}

	lowerTarget := strings.ToLower(strings.TrimSuffix(clean, "."))

	t.mu.Lock()
	if t.peerIPCache != nil {
		if ip, ok := t.peerIPCache[lowerTarget]; ok && ip != "" {
			t.mu.Unlock()
			return ip
		}
	}
	t.mu.Unlock()

	lc, err := srv.LocalClient()
	if err != nil {
		return clean
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	status, err := lc.Status(ctx)
	if err != nil {
		return clean
	}

	for _, p := range status.Peer {
		dns := strings.ToLower(strings.TrimSuffix(p.DNSName, "."))
		shortDNS := dns
		if dot := strings.IndexByte(shortDNS, '.'); dot > 0 {
			shortDNS = shortDNS[:dot]
		}
		if strings.EqualFold(p.HostName, lowerTarget) || dns == lowerTarget || shortDNS == lowerTarget {
			for _, addr := range p.TailscaleIPs {
				if addr.Is4() {
					ipStr := addr.String()
					t.mu.Lock()
					if t.peerIPCache == nil {
						t.peerIPCache = make(map[string]string)
					}
					t.peerIPCache[lowerTarget] = ipStr
					t.mu.Unlock()
					return ipStr
				}
			}
		}
	}
	return clean
}

// dial connects to host:port over the userspace tailnet.
func (t *Tsnet) dial(host string, port int) (net.Conn, error) {
	t.mu.Lock()
	srv := t.srv
	t.mu.Unlock()
	if srv == nil {
		return nil, fmt.Errorf("tsnet not started")
	}

	resolved := t.resolveTailnetHost(srv, host)
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	addr := net.JoinHostPort(resolved, fmt.Sprintf("%d", port))
	conn, err := srv.Dial(ctx, "tcp", addr)
	if err != nil {
		return nil, fmt.Errorf("tsnet dial %s: %w", addr, err)
	}
	return conn, nil
}

// StartForwarder opens a persistent multi-connection TCP proxy on 127.0.0.1:0
// that forwards all incoming local HTTP/audio/cover sockets to host:port over tsnet.
// Returns the local base URL "http://127.0.0.1:<port>".
func (t *Tsnet) StartForwarder(host string, port int) (string, error) {
	cleanHost := strings.TrimSpace(host)
	if cleanHost == "" {
		return "", fmt.Errorf("target host is empty")
	}
	if port <= 0 {
		port = 5480
	}
	targetKey := fmt.Sprintf("%s:%d", strings.ToLower(cleanHost), port)

	t.mu.Lock()
	if t.srv == nil {
		t.mu.Unlock()
		return "", fmt.Errorf("tsnet not started")
	}
	if t.fwdLn != nil && t.fwdTarget == targetKey && t.fwdURL != "" {
		existingURL := t.fwdURL
		t.mu.Unlock()
		return existingURL, nil
	}
	oldLn := t.fwdLn
	t.fwdLn = nil
	t.fwdTarget = ""
	t.fwdURL = ""
	t.mu.Unlock()

	if oldLn != nil {
		_ = oldLn.Close()
	}

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return "", fmt.Errorf("loopback forwarder listen: %w", err)
	}

	baseURL := "http://" + ln.Addr().String()

	t.mu.Lock()
	t.fwdLn = ln
	t.fwdTarget = targetKey
	t.fwdURL = baseURL
	t.mu.Unlock()

	go func() {
		for {
			down, acceptErr := ln.Accept()
			if acceptErr != nil {
				return
			}
			if tcpDown, ok := down.(*net.TCPConn); ok {
				_ = tcpDown.SetNoDelay(true)
			}
			go t.bridgeConn(down, cleanHost, port)
		}
	}()

	return baseURL, nil
}

func (t *Tsnet) bridgeConn(down net.Conn, host string, port int) {
	up, err := t.dial(host, port)
	if err != nil {
		time.Sleep(800 * time.Millisecond)
		up, err = t.dial(host, port)
		if err != nil {
			_ = down.Close()
			return
		}
	}

	done := make(chan struct{}, 2)
	go func() {
		_, _ = io.Copy(up, down)
		_ = up.Close()
		_ = down.Close()
		done <- struct{}{}
	}()
	go func() {
		_, _ = io.Copy(down, up)
		_ = down.Close()
		_ = up.Close()
		done <- struct{}{}
	}()
	<-done
}

// DialLoopback dials host:port over the tailnet for a single TCP connection.
func (t *Tsnet) DialLoopback(host string, port int) (string, error) {
	up, err := t.dial(host, port)
	if err != nil {
		return "", err
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		_ = up.Close()
		return "", fmt.Errorf("loopback listen: %w", err)
	}
	tcpLn, ok := ln.(*net.TCPListener)
	if !ok {
		_ = ln.Close()
		_ = up.Close()
		return "", fmt.Errorf("loopback listen: not a TCP listener")
	}
	_ = tcpLn.SetDeadline(time.Now().Add(30 * time.Second))
	go func() {
		defer tcpLn.Close()
		down, err := tcpLn.Accept()
		if err != nil {
			_ = up.Close()
			return
		}
		_ = tcpLn.Close()
		go func() {
			_, _ = io.Copy(up, down)
			_ = up.Close()
			_ = down.Close()
		}()
		_, _ = io.Copy(down, up)
		_ = up.Close()
		_ = down.Close()
	}()
	return ln.Addr().String(), nil
}
