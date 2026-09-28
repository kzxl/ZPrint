# ZPrint — Universal Zero-Configuration Network Print Bridge

ZPrint transforms any USB host-based / GDI / CAPT printer (such as **Canon LBP 2900 / 3000**, **HP LaserJet 1020**) and legacy printers into a high-performance, driverless **Network Smart IP Printer** across the entire local area network (LAN/WLAN).

---

## 1. The Chronic Problem Solved

### Traditional Windows Spooler Share (`\\host\printer`) Hurdles:
- **Error `0x0000011b` & `0x00000709`**: Caused by Windows RPC Print Spooler hardening (`RpcAuthnLevelPrivacyEnabled` & Point-and-Print CVE mitigations).
- **CAPT / Host-based Driver Traps**: Printers like the Canon LBP 2900 lack an onboard rasterizer or PCL/PostScript processor. Windows network sharing requires every client machine to install the identical 32/64-bit proprietary driver, which frequently crashes or refuses to print over SMB.
- **Concurrent Spooler Collisions**: Multiple client PCs printing at the same time cause spooler hangs or interleaved/corrupted paper output.

### The ZPrint Solution:
- **Zero Windows RPC/SMB Dependency**: Spools directly to the host's Win32 subsystem (`winspool.drv` RAW mode) — zero network RPC hurdles.
- **Wire Compression**: Employs pure C# `ZeroLz4` (1.6 GB/s) for ultra-fast, zero-allocation over-the-wire compression, reducing WiFi saturation.
- **Sequential FIFO Queueing**: Atomic `PrintJobQueue` guarantees jobs are processed sequentially with zero page interleaving.
- **Dual Client Modes**:
  1. **Option 1 (Virtual Driverless IPP)**: RFC 8011 IPP server allows macOS (AirPrint), Linux (CUPS), iOS, Android, and Windows to print transparently (`Ctrl+P` in Word/Excel/Chrome) without manufacturer drivers.
  2. **Option 2 (Desktop / CLI Tool)**: Drag-and-drop or command-line utility with page range filtering (`1, 3, 5-8`) and auto-discovery.

---

## 2. Architecture Overview

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
                                  [Canon LBP 2900 / USB]
```

---

## 3. CLI & Server Usage

### A. Run Host Server
Run on the PC directly connected to the printer via USB:
```bash
zprint server --printer "Canon LBP2900"
```
Options:
- `--port <port>`: Custom TCP port (default `9200`).
- `--ipp <port>`: Custom IPP port for AirPrint / driverless printing (default `6310`).
- `--printer <name>`: Target local printer name (defaults to Windows default printer).
- `--no-ipp`: Disable IPP server.
- `--no-discovery`: Disable UDP LAN auto-discovery.

### B. Client: Drag & Drop / CLI Printing (Option 2)
Drag and drop any file directly onto `zprint.exe`, or run from terminal:
```bash
zprint print document.pdf --pages "1, 3, 5-8" --copies 2
```
*Note: If `--server` is omitted, ZPrint automatically discovers the server on the LAN in < 1.5 seconds via UDP 9201.*

### C. Client: Scan LAN for Active Printers
```bash
zprint list
```
Displays:
```
Machine Name       IP Address       TCP Port IPP Port Shared Printers
------------------------------------------------------------------------------
DESKTOP-PRINT-01   192.168.1.105    9200     6310     Canon LBP2900
```

### D. Client: Setup Virtual Driverless Printer (Option 1)
Run `zprint install-printer` to view OS-specific connection commands.
- **Windows 10 / 11**: Add printer by URL: `http://<server-ip>:6310/ipp/printers/canon` using native **Microsoft IPP Class Driver**.
- **macOS**: Add IP printer with protocol **IPP (Internet Printing Protocol)** at `<server-ip>:6310` with queue `/ipp/`.
- **Linux**: `lpadmin -p ZPrint -E -v ipp://<server-ip>:6310/ipp/ -m everywhere`.

---

## 4. Building & Testing

Requires .NET 8.0 SDK:
```bash
dotnet build ZPrint.slnx
dotnet test ZPrint.slnx
```

---

## 5. License
Copyright (c) 2026 ZeroUniverse. All rights reserved.
