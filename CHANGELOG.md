# Changelog

## 1.1.0
- Port column sorts numerically (1/1/2 before 1/1/10).
- Convert a TRUNK port to ACCESS by setting an access VLAN (needs "Allow trunk and uplink ports"). Rollback restores the full trunk definition.
- New "Make trunk": native VLAN from a dropdown, allowed VLANs as "All VLANs" or a checklist. VLANs are checked against the switch VLAN list. BPDU guard, root guard, admin edge and loop protect are switched off automatically on the trunk.
- VLAN column shows native and allowed VLANs for trunk ports.
- Right pane scrolls; the log keeps a fixed height.

## 1.0.0
First public release.
- View the switches and ports of Aruba CX switches through the Aruba New Central Configuration API.
- Mass change on multiple ports of one switch: admin state, access VLAN, BPDU guard, root guard, admin edge port, loop protect, PoE and LAG membership.
- Dry run, Apply with confirmation, snapshot before each write, readback verification and Rollback from a snapshot file.
- Guards for trunk and uplink ports.
- Sign in secret stored encrypted with Windows DPAPI when you choose Remember.
