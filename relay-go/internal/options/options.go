// Package options ports Program.TryParse + RelayOptions (Validate) of the C# relay (plan-relay-go T1, seam S1).
package options

import (
	"errors"
	"fmt"
	"net/netip"
	"net/url"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/nxpanda-ai/panda-relay/relay-go/internal/auth"
)

// Usage is printed after a refused command line (Program.cs:18).
const Usage = "usage: panda-relay --public-host <host[:port]> [--alias-host <host[:port]>]... [--listen <ip>] [--port <n>] [--bytes-per-day <n>] [--streams-per-day <n>] [--trusted-proxy <ip|cidr>]... [--client-ip-header <name>] [--max-hosting <n>] [--internal-port <n>] [--noise-per-ip-per-hour <n>] [--hosted-admin-url <http://127.0.0.1:port> --hosted-admin-token-file <path> --hosted-mcp-url <https://host/mcp>]"

// Options is RelayOptions.cs: the defaults below are the C# relay's.
type Options struct {
	PublicHost    string     // the lowercase host[:port] extensions dial; RELAY_REGISTER signs it
	AliasHosts    []string   // plan-relay-rename D1: further hosts served (a renamed relay's old name); distinct, never PublicHost
	ListenAddress netip.Addr // default loopback
	Port          int        // 0 = any free port (tests)
	InternalPort  *int       // C5: a second listener on 127.0.0.1 for the hosted bridge's dials; nil = none

	MaxFrameBytes      int
	RegisterTimeout    time.Duration
	ControlIdleTimeout time.Duration
	AcceptTimeout      time.Duration
	SendTimeout        time.Duration
	CloseTimeout       time.Duration
	KeepAliveInterval  time.Duration
	KeepAliveTimeout   time.Duration

	BytesPerDay               int64
	StreamsPerDay             int
	RegistrationsPerIPPerHour int
	RefusedDialsPerIPPerHour  int
	NoisePerIPPerHour         int
	MaxTrackedNoise           int
	MaxTrackedIPs             int
	MaxBucketsPerPrefix       int

	TrustedProxies []netip.Prefix
	ClientIPHeader string

	HostedAdminURL   *url.URL // nil = no hosted bridge: RELAY_HOST / ROTATE / UNHOST answer unavailable
	HostedAdminToken string   // a secret: never printed
	HostedMCPURL     string
	MaxHosting       int
	HostedTimeout    time.Duration

	// hostedGiven: a hosted option was on the command line, even empty (C#'s null vs ""): the set is then checked
	hostedGiven bool
}

// Default returns RelayOptions' defaults for publicHost.
func Default(publicHost string) *Options {
	return &Options{
		PublicHost:                publicHost,
		ListenAddress:             netip.MustParseAddr("127.0.0.1"),
		Port:                      8080,
		MaxFrameBytes:             512 * 1024,
		RegisterTimeout:           10 * time.Second,
		ControlIdleTimeout:        60 * time.Second,
		AcceptTimeout:             15 * time.Second,
		SendTimeout:               30 * time.Second,
		CloseTimeout:              5 * time.Second,
		KeepAliveInterval:         20 * time.Second,
		KeepAliveTimeout:          20 * time.Second,
		BytesPerDay:               1 << 30,
		StreamsPerDay:             2000,
		RegistrationsPerIPPerHour: 600,
		RefusedDialsPerIPPerHour:  600,
		NoisePerIPPerHour:         600,
		MaxTrackedNoise:           200_000,
		MaxTrackedIPs:             100_000,
		MaxBucketsPerPrefix:       256,
		ClientIPHeader:            "X-Forwarded-For",
		MaxHosting:                32,
		HostedTimeout:             330 * time.Second,
	}
}

// digits parses NumberStyles.None: ASCII digits only (no sign, no space), within bits.
func digits(s string, bits int) (int64, bool) {
	if s == "" {
		return 0, false
	}
	for i := 0; i < len(s); i++ {
		if s[i] < '0' || s[i] > '9' {
			return 0, false
		}
	}
	v, err := strconv.ParseInt(s, 10, bits)
	return v, err == nil
}

// Parse is Program.TryParse: a refused command line returns an error whose text never holds a value (only the flag).
func Parse(args []string) (*Options, error) {
	var (
		host, hostedTokenFile, hostedMCP, clientIPHeader string
		hostSet, tokenFileSet, mcpSet, headerSet         bool
		listen                                           = netip.MustParseAddr("127.0.0.1")
		port                                             = 8080
		bytesPerDay, streamsPerDay, maxHosting           *int64
		internalPort, noise                              *int64
		adminURL                                         *url.URL
		proxies                                          []netip.Prefix
		aliases                                          []string
	)
	for i := 0; i < len(args); i++ {
		var value string
		has := i+1 < len(args)
		if has {
			value = args[i+1]
		}
		ok := has
		switch args[i] {
		case "--public-host":
			host, hostSet = value, ok
		case "--alias-host": // plan-relay-rename D1: checked by Validate (shape, once, not the primary)
			if ok {
				aliases = append(aliases, value)
			}
		case "--listen":
			a, err := netip.ParseAddr(value)
			ok = ok && err == nil && a.Zone() == ""
			if ok {
				listen = a.Unmap()
			}
		case "--port":
			v, good := digits(value, 32)
			ok = ok && good && v <= 65535
			if ok {
				port = int(v)
			}
		case "--bytes-per-day":
			v, good := digits(value, 64)
			ok = ok && good && v > 0
			bytesPerDay = &v
		case "--streams-per-day":
			v, good := digits(value, 32)
			ok = ok && good && v > 0
			streamsPerDay = &v
		case "--hosted-admin-url":
			u, err := url.Parse(value)
			ok = ok && err == nil && u.IsAbs()
			adminURL = u
		case "--hosted-admin-token-file":
			hostedTokenFile, tokenFileSet = value, ok
		case "--hosted-mcp-url":
			hostedMCP, mcpSet = value, ok
		case "--trusted-proxy":
			p, good := parseNetwork(value)
			ok = ok && good
			if ok {
				proxies = append(proxies, p)
			}
		case "--internal-port":
			v, good := digits(value, 32)
			ok = ok && good && v > 0 && v <= 65535
			internalPort = &v
		case "--noise-per-ip-per-hour":
			v, good := digits(value, 32)
			ok = ok && good
			noise = &v
		case "--max-hosting":
			v, good := digits(value, 32)
			ok = ok && good && v > 0
			maxHosting = &v
		case "--client-ip-header":
			clientIPHeader, headerSet = value, ok
		default:
			ok = false
		}
		if !ok {
			return nil, fmt.Errorf("bad argument %s", args[i]) // the flag only, never its value
		}
		i++
	}
	if !hostSet {
		return nil, errors.New("--public-host is required")
	}
	if headerSet && len(proxies) == 0 {
		return nil, errors.New("--client-ip-header needs --trusted-proxy")
	}
	var hostedToken string
	if tokenFileSet {
		b, err := os.ReadFile(hostedTokenFile)
		if err != nil {
			return nil, errors.New("cannot read --hosted-admin-token-file") // a secret: no error ever prints it
		}
		// File.ReadAllText drops a UTF-8 byte order mark (Notepad writes one); then .Trim()
		hostedToken = strings.TrimSpace(strings.TrimPrefix(string(b), "\xef\xbb\xbf"))
	}

	o := Default(host)
	o.ListenAddress = listen
	o.Port = port
	o.HostedAdminURL = adminURL
	o.HostedAdminToken = hostedToken
	o.HostedMCPURL = hostedMCP
	o.hostedGiven = adminURL != nil || tokenFileSet || mcpSet
	o.TrustedProxies = proxies
	o.AliasHosts = aliases
	if headerSet {
		o.ClientIPHeader = clientIPHeader
	}
	if maxHosting != nil {
		o.MaxHosting = int(*maxHosting)
	}
	if internalPort != nil {
		v := int(*internalPort)
		o.InternalPort = &v
	}
	if noise != nil {
		o.NoisePerIPPerHour = int(*noise)
	}
	if bytesPerDay != nil {
		o.BytesPerDay = *bytesPerDay
	}
	if streamsPerDay != nil {
		o.StreamsPerDay = int(*streamsPerDay)
	}
	if err := o.Validate(); err != nil {
		return nil, err
	}
	return o, nil
}

// parseNetwork is Program.TryParseNetwork: an address (/32, /128) or a CIDR whose host bits are zero. Refused: IPv4
// not written canonically ("010.0.0.1", "127.1"), IPv4-mapped IPv6, prefix 0, a zone.
func parseNetwork(value string) (netip.Prefix, bool) {
	text := value
	slash := strings.IndexByte(value, '/')
	if slash >= 0 {
		text = value[:slash]
	}
	a, err := netip.ParseAddr(text)
	if err != nil || a.Is4In6() || a.Zone() != "" || (a.Is4() && a.String() != text) {
		return netip.Prefix{}, false
	}
	if slash < 0 {
		return netip.PrefixFrom(a, a.BitLen()), true
	}
	p, err := netip.ParsePrefix(value)
	if err != nil || p.Bits() <= 0 || p.Masked().Addr() != a {
		return netip.Prefix{}, false
	}
	return p, true
}

var (
	hostShape   = regexp.MustCompile(`^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?(:[0-9]{1,5})?$`)
	mcpURLShape = regexp.MustCompile(`^[!-~]{1,300}$`)
	headerName  = regexp.MustCompile("^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,64}$")
)

func isLoopbackHost(h string) bool {
	if strings.EqualFold(h, "localhost") {
		return true
	}
	a, err := netip.ParseAddr(strings.Trim(h, "[]"))
	return err == nil && a.Unmap().IsLoopback()
}

// portOK: a URL's port is absent or 0-65535 (.NET Uri refuses a larger one).
func portOK(u *url.URL) bool {
	p := u.Port()
	if p == "" {
		return true
	}
	v, ok := digits(p, 32)
	return ok && v <= 65535
}

func (o *Options) validateHosted() error {
	if o.HostedAdminURL == nil && o.HostedAdminToken == "" && o.HostedMCPURL == "" && !o.hostedGiven {
		return nil
	}
	u := o.HostedAdminURL
	if u == nil || !strings.EqualFold(u.Scheme, "http") || u.Opaque != "" || (u.Path != "" && u.Path != "/") ||
		u.RawQuery != "" || u.ForceQuery || u.Fragment != "" || u.User != nil || !portOK(u) ||
		!(u.Hostname() == "127.0.0.1" || strings.EqualFold(u.Hostname(), "localhost")) {
		return errors.New("HostedAdminURL must be the hosted bridge's http://127.0.0.1:<port> admin origin")
	}
	if _, ok := auth.DecodeBase64URL(o.HostedAdminToken, 32); !ok {
		return errors.New("HostedAdminToken must be the hosted bridge's hosted-admin.token")
	}
	publicHost := strings.SplitN(o.PublicHost, ":", 2)[0]
	m, err := url.Parse(o.HostedMCPURL)
	if o.HostedMCPURL == "" || !mcpURLShape.MatchString(o.HostedMCPURL) || err != nil || !m.IsAbs() || m.Opaque != "" ||
		!portOK(m) || strings.ToLower(m.Hostname()) != publicHost || m.User != nil ||
		!(strings.EqualFold(m.Scheme, "https") || (strings.EqualFold(m.Scheme, "http") && isLoopbackHost(m.Hostname()))) {
		return errors.New("HostedMCPURL must be https://<public host>/… (http only on loopback)")
	}
	if o.HostedTimeout <= 0 {
		return errors.New("HostedTimeout: timeouts must be positive")
	}
	return nil
}

func publicPortInRange(host string) bool {
	i := strings.IndexByte(host, ':')
	if i < 0 {
		return true
	}
	v, ok := digits(host[i+1:], 32)
	return ok && v > 0 && v <= 65535
}

func isHost(h string) bool { return h != "" && hostShape.MatchString(h) && publicPortInRange(h) }

// MCPURLFor is RelayOptions.McpUrlFor (plan-relay-rename D1b): the MCP URL a Chrome registered for host is told —
// HostedMCPURL as configured for the primary; for an alias the same URL with that alias's name as its host (scheme,
// port and path kept), canonical as .NET's Uri.AbsoluteUri and the extension's URL.href write it (lowercase scheme and
// host, no default port).
func (o *Options) MCPURLFor(host string) string {
	if o.HostedMCPURL == "" || host == o.PublicHost {
		return o.HostedMCPURL
	}
	u, err := url.Parse(o.HostedMCPURL)
	if err != nil {
		return o.HostedMCPURL // validated at start (validateHosted): never
	}
	u.Scheme = strings.ToLower(u.Scheme)
	name := strings.SplitN(host, ":", 2)[0]
	port := u.Port()
	if port == "" || (u.Scheme == "https" && port == "443") || (u.Scheme == "http" && port == "80") {
		u.Host = name
	} else {
		u.Host = name + ":" + port
	}
	if u.Path == "" {
		u.Path = "/"
	}
	return u.String()
}

// Validate is RelayOptions.Validate: an error for a configuration the relay cannot run with.
func (o *Options) Validate() error {
	if !isHost(o.PublicHost) {
		return errors.New("PublicHost must be the lowercase host[:port] extensions dial")
	}
	seen := map[string]bool{o.PublicHost: true}
	for _, a := range o.AliasHosts {
		if !isHost(a) || seen[a] {
			return errors.New("each alias host must be a lowercase host[:port], once, and not the public host")
		}
		seen[a] = true
	}
	if !headerName.MatchString(o.ClientIPHeader) {
		return errors.New("ClientIpHeader must be an HTTP header name")
	}
	switch {
	case o.MaxFrameBytes < 1024:
		return errors.New("MaxFrameBytes must be at least 1024")
	case o.MaxTrackedIPs < 1, o.MaxBucketsPerPrefix < 1:
		return errors.New("the tracking caps must be at least 1")
	case o.MaxHosting < 1:
		return errors.New("MaxHosting must be at least 1")
	}
	if o.InternalPort != nil {
		ip := *o.InternalPort
		if ip < 0 || ip > 65535 || (ip != 0 && ip == o.Port) {
			return errors.New("InternalPort must be a free loopback port other than Port")
		}
	}
	if o.BytesPerDay < 0 || o.StreamsPerDay < 0 || o.RegistrationsPerIPPerHour < 0 || o.RefusedDialsPerIPPerHour < 0 ||
		o.NoisePerIPPerHour < 0 || o.MaxTrackedNoise < 0 {
		return errors.New("quotas and limits must not be negative")
	}
	if err := o.validateHosted(); err != nil {
		return err
	}
	if o.Port < 0 || o.Port > 65535 {
		return errors.New("Port must be 0-65535")
	}
	for _, d := range []time.Duration{o.RegisterTimeout, o.ControlIdleTimeout, o.AcceptTimeout, o.SendTimeout,
		o.CloseTimeout, o.KeepAliveInterval, o.KeepAliveTimeout} {
		if d <= 0 {
			return errors.New("timeouts must be positive")
		}
	}
	return nil
}
