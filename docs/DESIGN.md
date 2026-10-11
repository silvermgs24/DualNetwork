# Dual Network — routing design

This document was written before the routing engine was implemented. It explains
the approach, the Windows behaviour it relies on, and the situations where the
application refuses to change anything automatically.

## 1. How Windows picks a route (the root of the problem)

For every outgoing IPv4 packet Windows:

1. picks the route with the **longest matching prefix**, then
2. among equal prefixes, the lowest **route metric + interface metric**.

When Ethernet and Wi-Fi are both connected and **both have a default gateway**,
the table holds two `0.0.0.0/0` routes. Wired adapters usually get a lower
automatic interface metric (they're faster), so the client's default route wins
and Internet traffic goes to a network that has no Internet. Dead-gateway
detection can also flip between the two defaults, which makes it unpredictable.

When Ethernet has **no gateway**, there is no competing default route. Windows
already sends the client subnet over Ethernet (its on-link route is longer than
`/0`) and everything else over Wi-Fi. In that case the only remaining problem
is DNS.

## 2. Approach: additive, non-persistent, minimal

The engine never edits or deletes a route it did not create. It only **adds**
routes, and only in the **active (non-persistent) store**. A reboot therefore
always returns Windows to its own configuration.

| Problem | Change (only when needed) | Why this works |
|---|---|---|
| Ethernet has a default route that competes with Wi-Fi | Add `0.0.0.0/1` and `128.0.0.0/1` via the Wi-Fi gateway | Two `/1` routes are more specific than any `/0`, so they win over the client's default route **regardless of metrics or DHCP renewals**. The client's own default route stays where it is and is never touched. |
| The client's default route used to carry traffic to *other* client subnets (behind the client gateway) | Add the RFC 1918 block that contains the Ethernet address (e.g. `10.0.0.0/8`) via the client gateway. Optional: all private blocks and user-defined prefixes. | Internal destinations that are not on-link still go to the client gateway. Routes the client already supplies (static or DHCP option 121) are longer, so they keep winning and are left untouched. |
| A client DNS server is outside the client subnet and not covered by the routes above | `/32` host route via the client gateway | Internal DNS stays reachable. |
| A Wi-Fi DNS server would be captured by a client range | `/32` host route via the Wi-Fi gateway | Public DNS stays on Wi-Fi. |
| Internal names | NRPT rule per internal DNS suffix (from Ethernet's DHCP/connection suffix, plus suffixes the user adds) → client DNS servers | `*.corp.local` always goes to the client DNS. Other names keep Windows' normal resolution. The client's DNS settings are never replaced. |

Why not change the interface metric? It is a single knob with side effects. It
also reorders the DNS servers Windows tries first, and DHCP or the network
profile can overwrite it, so it does not survive changes on its own. Why not
delete the Ethernet default route? That breaks every internal subnet behind
the client gateway, and DHCP adds it back on the next renewal.

Why `/1` routes and not a `0.0.0.0/0` with a lower metric? A `/0` with a lower
metric still competes on metrics, and metrics can change under us. A `/1` pair
wins on prefix length, which Windows evaluates first. OpenVPN and many other
tools use the same technique.

### Role selection

* **Internet adapter**: the connected Wi-Fi adapter with an IPv4 default
  gateway. Settings can override it (for example, USB tethering).
* **Client adapter**: the connected, non-virtual Ethernet adapter with a usable
  IPv4 address. Settings can override it.
* VPN and virtual adapters (Hyper-V, VMware VMnet, VirtualBox, WSL) are never
  chosen automatically and never modified.

## 3. Safety model

1. **Snapshot first.** Before the first change, the app writes the full IPv4
   route table, interface metrics, IP configuration, DNS servers and existing
   NRPT rules to `%ProgramData%\DualNetwork\snapshots\`.
2. **Journal every change.** Each route or NRPT rule the app creates is written
   to `%ProgramData%\DualNetwork\state.json` *immediately after* it succeeds. If
   Windows reports that a route already exists, it is **not** journaled, so it
   will never be removed.
3. **Restore = remove exactly what the journal lists**, in reverse order. The
   app then compares the current state with the snapshot and reports any
   remaining differences, such as DHCP changes made by the client network. It
   does not force stale routes back.
4. **Crash, kill, reboot.** Routes are non-persistent, so a reboot clears them.
   NRPT rules are tagged with the comment `DualNetwork`. At startup, any
   leftover journal or tagged rule is cleaned up and the cleanup is logged.
5. **Re-check before apply.** The plan shown in the confirmation dialog is
   recomputed right before applying. If the network changed in the meantime,
   nothing is applied and the user reviews the new plan.
6. **Never** disable an adapter, change an interface metric, edit DNS server
   lists, touch a VPN interface or its routes, add a `/0` route, or delete a
   route that is not in the journal.

## 4. Monitoring and reconciliation

While Dual Network Mode is on, the app listens for address, interface and route
change notifications (`NotifyRouteChange2`, `NotifyIpInterfaceChange`,
`NetworkChange`) and also polls every 30 s. Events are debounced. On every
change it computes a new plan and reconciles it against the journal:

* routes in the journal that are no longer wanted are removed (old Wi-Fi
  gateway, Ethernet unplugged, VPN started);
* wanted routes that are missing are added (new Wi-Fi gateway, new DHCP lease);
* if the new plan is a refusal (see below), every journaled change is removed
  and the mode waits in *standby* until conditions allow it again.

So:

* **Ethernet unplugged** → all additions removed; plain Wi-Fi routing.
* **Wi-Fi unplugged** → additions removed; Ethernet is left exactly as the
  client configured it.
* **DHCP change on Ethernet** → client ranges and DNS routes are recomputed.

## 5. When the app refuses to change routes

The plan is **refused** (nothing applied, or everything removed if already
active) when:

| Condition | Reason |
|---|---|
| No connected Ethernet adapter with a usable IPv4 address | Nothing to protect |
| Ethernet only has a `169.254.x.x` address | DHCP failed; there is no client network |
| No connected Wi-Fi adapter with an IPv4 default gateway | No Internet path to route to |
| The Ethernet and Wi-Fi subnets overlap | Destinations are ambiguous; any rule would break one side |
| A **full-tunnel VPN** owns the default route (`/0` or `/1` on a VPN adapter) | Overriding it would leak traffic outside the VPN |
| Another (non-VPN) tool has already installed `/1` routes | We would fight it |
| Wi-Fi has no Internet but Ethernet does | Nothing to fix; Ethernet *is* the Internet path |
| Internet and client roles resolve to the same adapter | Invalid configuration |

Individual changes are **skipped with a warning** (the rest of the plan still applies) when:

* a client range is equal to or inside the Wi-Fi subnet;
* a client range would capture traffic of a VPN route that is broader or of
  equal length (a split-tunnel VPN);
* a client range already exists on another non-VPN adapter;
* an NRPT rule for the same namespace already exists and is not ours.

## 6. Known Windows limitations

* **IPv6.** This version manages IPv4 only. If the client network advertises
  an IPv6 default route but has no Internet, IPv6-first applications fall back
  to IPv4 after a delay (Happy Eyeballs). The app detects this and warns.
* **Unknown internal subnets.** If the client has internal networks outside the
  private block of the Ethernet address (for example, a public range used
  internally) and does not push routes for them, the app cannot discover them.
  Add them under *Settings → Additional client networks*.
* **Unqualified / single-label names** are resolved by Windows across all
  adapters. NRPT only applies to namespaces, so add internal suffixes when the
  client does not supply one through DHCP.
* **Full-tunnel VPNs.** By design, they win, and the app stands down.
* **Administrator rights** are required to add or remove routes and NRPT
  rules. Viewing, diagnostics and tests work without elevation.
* **Group Policy NRPT.** Rules pushed by domain Group Policy take precedence
  over local NRPT rules. The app reports existing rules but cannot override
  policy.
