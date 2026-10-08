# VEINS — VPN Easy Install

Automated VPN server provisioner for Windows. Connect to your VPS via SSH, pick a protocol, click Install — done.

## Supported protocols

| Protocol | Transport | Notes |
|----------|-----------|-------|
| **Xray VLESS/Reality** | TCP 443 | TLS-lookahead resistant, SNI masquerade |
| **Hysteria 2** | QUIC UDP+TCP 443 | Brutal congestion control, self-signed EC cert |
| **Trojan** | TCP 443 | TLS-mimicking, self-signed EC cert |
| **AmneziaWG** | UDP (WireGuard-based) | Obfuscated WireGuard (Jc/Jmin/Jmax) |

## How it works

1. Enter your VPS IP, SSH port, username, and password (or SSH key path)
2. Select a protocol and optional SNI domain
3. Click **Install**
4. Get a ready-to-use client link / config

The app connects via SSH, detects the OS/arch, downloads or uploads server binaries, generates keys on the server, writes configs, sets up systemd services, firewall rules, and BBR tuning.

## Requirements

- Windows 10/11 x64
- .NET 8 Desktop Runtime (for framework-dependent build)
- A Linux VPS (Ubuntu 20.04+, Debian 11+, etc.) with root or sudo access

## Download

| File | Description |
|------|-------------|
| `VEINS-setup.exe` | Installer (installs to Program Files, creates shortcuts) |
| `VEINS-portable.zip` | Portable — extract and run `VEINS.exe`, no install needed |

## Build from source

```powershell
git clone https://github.com/zhenyasreda/VEINS.git
cd VEINS
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## License

MIT © 2026 Zhenya Sreda
