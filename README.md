# ZPrint — Universal Zero-Configuration Network Print Bridge & RFC 8011 IPP Server

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET Multi-Target](https://img.shields.io/badge/.NET-8.0%20%7C%20Standard%202.0%20%7C%20Framework%204.6.2-purple.svg)](https://dotnet.microsoft.com/)
[![Zero External Dependencies](https://img.shields.io/badge/Dependencies-0%20External%20NuGet-brightgreen.svg)](#zero-dependency-architecture)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux%20%7C%20iOS%20%7C%20Android-lightgrey.svg)](#client-setup-walkthrough)
[![Tests Passing](https://img.shields.io/badge/Tests-100%25%20Passing-success.svg)](#building--testing)

```
  ███████╗██████╗ ██████╗ ██╗███╗   ██╗████████╗
  ╚══███╔╝██╔══██╗██╔══██╗██║████╗  ██║╚══██╔══╝
    ███╔╝ ██████╔╝██████╔╝██║██╔██╗ ██║   ██║   
   ███╔╝  ██╔═══╝ ██╔══██╗██║██║╚██╗██║   ██║   
  ███████╗██║     ██║  ██║██║██║ ╚████║   ██║   
  ╚══════╝╚═╝     ╚═╝  ╚═╝╚═╝╚═╝  ╚═══╝   ╚═╝   
  Universal LAN Zero-Configuration Print Bridge
```

**ZPrint** transforms any USB host-based, GDI, or CAPT printer (such as **Canon LBP 2900 / 3000**, **HP LaserJet 1020 / P1005**, **Epson**, and legacy office printers) into a high-performance, driverless **Network Smart IP Printer** across your entire local area network (LAN/WLAN).

With built-in **RFC 8011 IPP (Internet Printing Protocol)** support, mobile devices (iPhone, iPad, Android) and desktop operating systems (macOS, Linux, Windows) can print seamlessly using their native print dialog (`Ctrl+P` or **Share -> Print**) with **zero manufacturer driver installation required**.

---

## Table of Contents
1. [The Problem Solved](#the-problem-solved)
2. [Key Capabilities](#key-capabilities)
3. [Architecture Overview](#architecture-overview)
4. [Feature Comparison](#feature-comparison)
5. [Quick Start](#quick-start)
6. [Client Setup Walkthrough](#client-setup-walkthrough)
   - [Windows 10 / 11](#windows-10--11-microsoft-ipp-class-driver)
   - [macOS](#macos-airprint--driverless-ipp)
   - [Linux](#linux-cups-ipp-everywhere)
   - [iOS & iPadOS](#ios--ipados-airprint)
   - [Android](#android-mopria--default-print-service)
   - [ZPrint CLI & Drag-and-Drop Tool](#zprint-cli--drag-and-drop-client)
7. [Host Server Administration](#host-server-administration)
   - [User Login Autostart](#user-login-autostart)
   - [System Boot Service (Unattended SYSTEM)](#system-boot-service-unattended-system)
   - [Automatic In-Place Updates (ZUpdate)](#automatic-in-place-updates-zupdate)
8. [CLI Reference](#cli-reference)
9. [Zero-Dependency Architecture](#zero-dependency-architecture)
10. [Building & Testing](#building--testing)
11. [License](#license)

---

## The Problem Solved

### Traditional Windows SMB Printer Sharing (`\\host\printer`) Pitfalls:
1. **Windows Spooler Hardening Errors (`0x0000011b` & `0x00000709`)**:
   - Recent Windows security updates enforce strict RPC packet privacy (`RpcAuthnLevelPrivacyEnabled`) and restrict Point-and-Print due to PrintNightmare mitigations.
   - Connecting to a shared printer from other PCs routinely fails with cryptic error codes requiring registry hacks, disabled UAC, or downgraded security policies.
2. **The CAPT / Host-based Driver Trap**:
   - Iconic workhorse printers like the **Canon LBP 2900 / 3000** have no onboard rasterizer, PCL engine, or PostScript chip. They rely on host CPU rasterization (CAPT / GDI).
   - Windows network sharing requires every client PC to install the identical 32/64-bit proprietary driver. These drivers frequently crash or refuse to communicate over SMB network pipes.
   - Non-Windows devices (macOS, iOS, Linux, Android) are completely locked out.
3. **Spooler Queue Collisions & Paper Jams**:
   - When multiple workstations send print jobs simultaneously over Windows SMB, the spooler often deadlocks, drops jobs, or interleaves pages between separate documents.

### How ZPrint Solves It:
- **Direct Win32 RAW Spooling**: Communicates directly with the local host's Win32 printing subsystem (`winspool.drv` in raw data mode). Completely bypasses Windows network RPC/SMB sharing and its security errors.
- **Universal Driverless IPP (RFC 8011)**: Emulates an IPP Everywhere / AirPrint print server. Client devices render documents to standard raster/PDF and send them over HTTP/IPP; the host handles local spooling.
- **ZeroLz4 Wire Compression**: Custom LZ4 stream compression written in pure C# (1.6 GB/s) delivering 70–85% payload reduction and sub-millisecond LAN latency.
- **Thread-Safe FIFO Queueing**: Atomic `PrintJobQueue` guarantees jobs are processed sequentially with zero page interleaving.
- **Auto-Discovery via UDP 9201**: Clients and CLI tools discover active ZPrint servers on the LAN in < 1.5 seconds without hardcoded IP configurations.

---

## Key Capabilities

- **Zero External Dependencies**: Pure C# BCL implementation. 0 third-party NuGet packages for maximum security, auditability, and instant startup.
- **Multi-Target Compatibility**: Compiles seamlessly on `.NET 8.0`, `.NET Standard 2.0`, and `.NET Framework 4.6.2`.
- **Integrated Background Daemon**: Hidden console execution on Windows via Win32 P/Invoke (`ShowWindow(SW_HIDE)`).
- **Dual Autostart Scopes via `ZeroSystem.StartupManager`**:
  - *User Login Autostart*: Windows Registry `HKCU\...\Run`, Linux `~/.config/autostart`, macOS `LaunchAgents`.
  - *System Boot Service*: Windows Task Scheduler running under `SYSTEM` at machine boot (before login), Linux `systemd`.
- **Integrated Atomic Auto-Updater**: Native hand-off to ZeroUniverse's `ZUpdate` engine for zero-downtime binary upgrades.

---

## Architecture Overview

```
                      +------------------------------------------+
                      |               Client Nodes               |
                      |                                          |
                      |  [macOS / iOS]   [Linux]   [Windows PC]  |
                      |    (AirPrint)    (CUPS)      (Ctrl+P)    |
                      +-------+-------------+------------+-------+
                              |             |            |
             RFC 8011 IPP     |             |            |  ZPrint CLI / Tool
        (application/ipp)     |             |            |  (ZeroLz4 TCP)
                              v             v            v
                      +------------------------------------------+
                      |         ZPrint Host Server Node          |
                      |                                          |
                      |  +--------------------+  +------------+  |
                      |  | ZPrintIppServer    |  | TCP Server |  |
                      |  | (Port 6310)        |  | (Port 9200)|  |
                      |  +---------+----------+  +-----+------+  |
                      |            |                   |         |
                      |            |   (ZeroLz4 Decomp)|         |
                      |            +---------+---------+         |
                      |                      |                   |
                      |                      v                   |
                      |            +-------------------+         |
                      |            |   PrintJobQueue   |         |
                      |            |  (FIFO Serializer)|         |
                      |            +---------+---------+         |
                      |                      |                   |
                      |                      v                   |
                      |            +-------------------+         |
                      |            |   WindowsSpooler  |         |
                      |            | (winspool.drv RAW)|         |
                      |            +---------+---------+         |
                      |                      |                   |
                      +----------------------|-------------------+
                                             v
                             [Canon LBP 2900 / HP 1020 / USB]
```

---

## Feature Comparison

| Feature | Windows SMB Share (`\\host\printer`) | Dedicated Hardware Print Server | ZPrint Bridge |
|---|:---:|:---:|:---:|
| **Canon LBP 2900 / CAPT Support** | ❌ Unreliable / Crashes | ❌ Fails (No CAPT on hardware) |  **100% Native & Stable** |
| **0x0000011b & 0x00000709 Errors** | ❌ Frequent | ⚠️ Protocol Mismatches |  **Immune (Direct RAW Spool)** |
| **Driverless Printing (No Client Driver)**| ❌ Driver required on every PC | ⚠️ Requires PostScript/PCL |  **Native (AirPrint / IPP)** |
| **macOS & iOS (AirPrint)** | ❌ Not supported | ⚠️ Model-dependent |  **Plug & Play** |
| **Android & Linux (CUPS)** | ❌ Complex SMB setup | ⚠️ PCL/PS only |  **Standard IPP Everywhere** |
| **Wire Compression** | ❌ None (Raw network SMB) | ❌ None |  **ZeroLz4 (1.6 GB/s)** |
| **Concurrent Job FIFO Queue** | ⚠️ Spooler lockups | ⚠️ Hardware buffer overflow |  **Thread-safe FIFO Queue** |
| **Automatic LAN Discovery** | ⚠️ NetBIOS / WS-Discovery lags | ⚠️ Fixed IP / Web GUI |  **UDP 9201 in < 1.5s** |
| **Cost** | Free (but buggy) | $40 – $100 per device |  **Free & Open Source** |

---

## Quick Start

### 1. Run Host Server
Connect your printer to a Windows PC via USB and start the server:
```bash
# Automatically picks your Windows default printer
zprint server

# Or specify a printer explicitly
zprint server --printer "Canon LBP2900"
```

### 2. Run in the Background
To detach from the console and run silently in the background:
```bash
zprint server --printer "Canon LBP2900" --background
```
*The console window immediately hides. Logs are written to `%LOCALAPPDATA%\ZPrint\zprint-server.log`.*

---

## Client Setup Walkthrough

### Windows 10 / 11 (Microsoft IPP Class Driver)
*No Canon or manufacturer driver needed on the client PC!*
1. Open **Settings** -> **Bluetooth & devices** -> **Printers & scanners** -> **Add device**.
2. Click **Add manually** (The printer that I want isn't listed).
3. Select **Select a shared printer by name** and enter:
   ```
   http://<SERVER_IP>:6310/ipp/printers/canon
   ```
4. Click **Next**. Windows automatically binds the native **Microsoft IPP Class Driver**.
5. You're ready to print! Use `Ctrl+P` in any application.

---

### macOS (AirPrint / Driverless IPP)
1. Open **System Settings** -> **Printers & Scanners** -> **Add Printer, Scanner, or Fax**.
2. Select the **IP** tab (Globe icon).
3. Enter settings:
   - **Address**: `<SERVER_IP>:6310`
   - **Protocol**: `Internet Printing Protocol - IPP`
   - **Queue**: `/ipp/`
   - **Name**: `Canon LBP2900 (Network)`
   - **Use**: `Generic PostScript Printer` (or `Auto Select`)
4. Click **Add**.

---

### Linux (CUPS / IPP Everywhere)
Run via terminal or add through GNOME / KDE Printer Settings:
```bash
# Register driverless IPP printer with CUPS
lpadmin -p ZPrint_Canon -E -v ipp://<SERVER_IP>:6310/ipp/ -m everywhere

# Verify printer status
lpstat -p ZPrint_Canon
```

---

### iOS & iPadOS (AirPrint)
1. Ensure your iPhone/iPad is connected to the same Wi-Fi network.
2. In any app (Safari, Photos, Mail, Files), tap the **Share** button -> **Print**.
3. Tap **Select Printer**.
4. Tap **Canon LBP2900** (detected automatically via IPP/mDNS).
5. Tap **Print**.

---

### Android (Mopria / Default Print Service)
1. Open **Settings** -> **Connected devices** -> **Connection preferences** -> **Printing**.
2. Select **Default Print Service** or **Mopria Print Service**.
3. Choose **Add printer by IP address**:
   - Address: `<SERVER_IP>:6310`
4. Print directly from Chrome, Google Drive, or Gallery.

---

### ZPrint CLI & Drag-and-Drop Client
ZPrint also provides a high-performance desktop client for batch printing:
```bash
# Drag & drop any PDF/file onto zprint.exe, or run:
zprint print invoice.pdf

# Specify page ranges and multiple copies:
zprint print contract.pdf --pages "1, 3, 5-8" --copies 2

# Discover all active ZPrint servers on the LAN:
zprint list
```

Output:
```
Machine Name       IP Address       TCP Port IPP Port Shared Printers
------------------------------------------------------------------------------
DESKTOP-PRINT-01   192.168.1.105    9200     6310     Canon LBP2900
```

---

## Host Server Administration

### User Login Autostart
Configures ZPrint to start silently in the background whenever the current user logs into Windows:
```bash
# Enable autostart with specific printer
zprint autostart enable --printer "Canon LBP2900"

# Check current autostart status
zprint autostart status

# Disable autostart
zprint autostart disable
```
*Platform Implementation: Windows Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | Linux `~/.config/autostart/zprint.desktop` | macOS `~/Library/LaunchAgents/`.*

---

### System Boot Service (Unattended SYSTEM)
Configures ZPrint to start as a background system service when the machine boots up, **even before any user logs in**.
> [!IMPORTANT]
> Must be executed from an elevated terminal (**Run as Administrator** on Windows, or `sudo` on Linux).

```bash
# Install and register system boot task
zprint service install --printer "Canon LBP2900"

# Start the service immediately
zprint service start

# Check service status
zprint service status

# Stop service
zprint service stop

# Uninstall service
zprint service uninstall
```
*Platform Implementation: Windows Task Scheduler (`schtasks.exe /sc onstart /ru SYSTEM /rl HIGHEST`) | Linux `systemd` unit (`/etc/systemd/system/zprint.service`).*

---

### Automatic In-Place Updates (ZUpdate)
ZPrint integrates seamlessly with ZeroUniverse's `ZUpdate` engine for atomic, in-place binary upgrades:
```bash
# Check if a new version is available on GitHub Releases
zprint update --check

# Download package and perform atomic in-place upgrade
zprint update

# Force download and reinstall current or newer version
zprint update --force

# Suppress user confirmation prompts during update execution
zprint update --silent
```

---

## CLI Reference

```
Usage: zprint <command> [options]

Commands:
  server              Start the print server daemon on the host PC
  print <file>        Send a document to a ZPrint server
  list                Discover all active ZPrint servers on the LAN
  autostart <action>  Configure user login autostart (enable, disable, status)
  service <action>    Manage system boot service (install, uninstall, start, stop, status)
  update              Check or perform atomic in-place updates via ZUpdate
  install-printer     Display driverless IPP setup instructions for all platforms

Server Options:
  --port <port>       Custom TCP port for ZPrint protocol (default: 9200)
  --ipp <port>        Custom IPP port for AirPrint/driverless clients (default: 6310)
  --printer <name>    Target local printer name (default: Windows default printer)
  --background, -b    Run daemon silently in background (hides console window)
  --no-ipp            Disable RFC 8011 IPP server
  --no-discovery      Disable UDP LAN auto-discovery responder

Print Options:
  --server <ip:port>  Target ZPrint server (auto-discovered if omitted)
  --printer <name>    Target printer name on remote server
  --pages <range>     Page range filter (e.g., "1, 3, 5-8", "2-")
  --copies <num>      Number of copies (default: 1)

Update Options:
  --check             Check GitHub Releases without downloading or replacing binaries
  --force             Force download and reinstall even if already up-to-date
  --silent            Execute update without interactive console prompts
```

---

## Zero-Dependency Architecture

ZPrint adheres strictly to the ZeroUniverse engineering philosophy:
- **0 External NuGet Packages**: Every byte of networking, compression, serialization, and protocol parsing is implemented with pure C# BCL.
- **Ultra-Lightweight Footprint**: Memory usage typically stays under **15 MB RAM**, making it ideal for running continuously in the background on office PCs or dedicated print boxes.
- **Sovereign Reliability**: No third-party dependency vulnerabilities, no version drift, and instant cold startup.

---

## Building & Testing

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)

### Build Solution
```bash
dotnet build ZPrint.slnx
```

### Run Unit Tests
```bash
dotnet test ZPrint.slnx
```

### Publish Lightweight Single-File Executable
In accordance with ZeroUniverse publication guidelines (framework-dependent single file):
```bash
dotnet publish src/ZPrint.Tool/ZPrint.Tool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/zprint-win-x64
```

---

## License

This project is licensed under the [MIT License](LICENSE).  
Copyright (c) 2026 Phong Võ (kzxl) / ZeroUniverse. All rights reserved.
