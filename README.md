# PortPatrol

PortPatrol is a Windows desktop app (WPF, .NET 8, C#) to view and change port settings of Aruba CX switches through the HPE Aruba Networking Central (Aruba New Central) Configuration API.

> Unofficial tool. Not affiliated with, endorsed by or supported by Hewlett Packard Enterprise. "Aruba" and "HPE" are trademarks of their respective owners. It uses an alpha API that can change without notice. Use at your own risk and test on a non-critical switch first.

## What it does
- Connects with your own Central API client (client ID and secret) and loads the switches after you sign in. Nothing about any environment is stored in the application.
- Three panes: switches, ports of the selected switch (click a port to expand its configuration), and the settings to change.
- Mass change on multiple ports of one switch: admin state, access VLAN, BPDU guard, root guard, admin edge port, loop protect (TX_DISABLE), PoE on or off, and LAG membership (leave, join or create a LAG).
- Dry run (live read, no write), Apply (only after a matching dry run, confirmed by typing the port count, snapshot before each write, readback verification, stops at the first problem), Refresh and Rollback from a snapshot file.
- Guards: trunk ports and ports whose description looks like an uplink (UL, uplink, LAG, backbone) are skipped unless you tick "Allow trunk and uplink ports". The access VLAN is only applied to ACCESS ports.

## License
MIT, see `LICENSE`.

## Requirements
- Windows 10 or 11 and the .NET 8 Desktop Runtime (or build the self contained variant).
- An Aruba New Central API client with permission to read monitoring data and write the network configuration. Classic Aruba Central is not supported.
- The base URL of your Central cluster (see "Finding your base URL" on the HPE Aruba Networking developer hub).

## Build
Install the .NET 8 SDK and run `build.cmd`, or `dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`.

## Use
1. Start the app, enter the base URL, client ID and client secret. "Remember" stores the secret encrypted with Windows DPAPI for the current Windows user only.
2. Select a switch. Check the device function (ACCESS_SWITCH, AGG_SWITCH or CORE_SWITCH) next to the port filter. If Central does not report it, the app assumes ACCESS_SWITCH and says so. Your choice is remembered on this PC.
3. Select ports, set the fields to change, run a dry run, then Apply.
4. Changes reach the physical switch after roughly 2 to 2.5 minutes. Refresh shows the configuration in Central. The Link column comes from Central monitoring and can lag a few minutes.

## Files on your PC
- `%AppData%\PortPatrol\settings.json` (optional remembered sign in, encrypted with Windows DPAPI). The device function per switch is kept in memory only and is forgotten when the app closes.
- `%LocalAppData%\PortPatrol\Runs` with logs and rollback snapshots (JSONL).

## Known limits
- Switch listing uses `GET /network-monitoring/v1/switches` and tolerant field mapping. If your tenant returns the data differently, the log shows the first raw item.
- Partial PATCH bodies (single fields) are less tested than the combined hardening body.
