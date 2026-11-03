//go:build android
// +build android

package tsnet

import (
	"net"

	"github.com/wlynxg/anet"
	"tailscale.com/net/netmon"
)

func init() {
	// Android 11+ (API 30) блокирует Netlink RTM_GETLINK для обычных приложений:
	// net.Interfaces() -> route ip+net: netlinkrib: permission denied
	// tailscale.com/net/netmon оборачивает net.Interfaces() и позволяет
	// подменить источник через RegisterInterfaceGetter. Используем anet
	// который читает интерфейсы через RTM_GETADDR + ioctl (без Bind) и
	// соответствует NetworkInterface.getNetworkInterfaces().
	netmon.RegisterInterfaceGetter(func() ([]netmon.Interface, error) {
		ifs, err := anet.Interfaces()
		if err != nil {
			return nil, err
		}
		ret := make([]netmon.Interface, len(ifs))
		for i := range ifs {
			// anet не отдаёт Addrs через Interface.Addrs() — берём через anet
			addrs, _ := anet.InterfaceAddrsByInterface(&ifs[i])
			ret[i] = netmon.Interface{
				Interface: &ifs[i],
				AltAddrs:  addrs,
			}
		}
		return ret, nil
	})
}

// Ensure anet import is used on android even if netmon not yet init
var _ = net.ParseIP
