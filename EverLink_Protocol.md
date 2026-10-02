# EverLink Wire Protocol

This document is the single source of truth for the serial protocol spoken between
EverLink Host (the PC app) and any EverLink Relay firmware. Both `Host/SerialLink.cs`
and `Relay.ino` (or any forked relay firmware) must match this exactly. If you change
the wire format, update this file in the same change.

The link is a full-duplex UART, currently always opened at **921600 baud**.

---

## 1. Identification (Host -> Relay -> Host)

Host owns the COM port and initiates everything. To find out whether a given serial
port has an EverLink Relay on the other end (and what kind), Host sends a single byte:

```
0xFE            (PING_BYTE)
```

A genuine Relay replies with a single line of ASCII text, terminated with `\n`:

```
IAM:EverLink:v<version>:<mac>:<chipModel>[:<kind>:<modes>:<activeMode>]
```

| Field | Meaning |
|---|---|
| `version` | Protocol version this firmware speaks. Integer, no leading zeros. See "Protocol versions" below. |
| `mac` | 12 hex characters, no separators (e.g. `B0CBD8CCBEF0`) - the chip's factory-burned unique MAC. |
| `chipModel` | Free text chip identity string (e.g. `ESP32-S3`). `Unknown` if not available. |
| `kind` | **v3+ only.** Free-text firmware identifier - see "Kind" below. Omitted entirely (not even a trailing colon) by v1/v2 firmware. |
\1| `activeMode` | **v3+ only.** Decimal index (into the `modes` list, zero-based) of the mode that is active right now. Omitted by v1/v2 firmware; if missing or unparseable Host assumes `0`. |\n
Only ever sent in reply to a ping - never spontaneously. This lets Host ask "are you
a Relay?" at any time (manual rescan), regardless of how long the board's been powered,
without relying on a boot-time announcement Host might have missed.

Reserved character: **`:` and `,` and `|` must never appear inside `kind` or a mode's
display name.** Host splits the ident line on `:` and a mode list on `,`/`|`; a
firmware-supplied string containing those characters will corrupt the parse. Stick to
letters, numbers, spaces, and basic punctuation like `-` and `'`.

### Protocol versions

| Version | Added |
|---|---|
| v1 | Base packet protocol + identification ping/reply (`mac`, `chipModel` only). |
| v2 | Rumble forwarding (`RMBL:` lines, see section 3). |
| v3 | `kind` and `modes` fields in the ident reply (see sections 4-5), the mode-switch command (Host -> Relay, section 5), and pairing status reporting (Relay -> Host, section 6). |

A Relay reporting a version newer than the Host build understands is treated as the
newest version Host *does* understand (see `DeviceInfo.EffectiveProtocolVersion`) -
forward compatible by design, so a future v4 Relay still works with an older Host,
just without whatever v4 adds.

---

## 2. Controller state packets (Host -> Relay)

Sent continuously by Host at a steady rate (currently every 4ms / 250Hz) once a
controller is assigned to a Relay. 14 bytes, fixed layout:

| Offset | Bytes | Field |
|---|---|---|
| 0 | 1 | Sync byte, always `0xA5` |
| 1-2 | 2 | Buttons, little-endian `uint16` (see bit layout below) |
| 3 | 1 | Left trigger, `uint8` 0-255 |
| 4 | 1 | Right trigger, `uint8` 0-255 |
| 5-6 | 2 | Left stick X, little-endian `int16` |
| 7-8 | 2 | Left stick Y, little-endian `int16` |
| 9-10 | 2 | Right stick X, little-endian `int16` |
| 11-12 | 2 | Right stick Y, little-endian `int16` |
| 13 | 1 | Checksum: XOR of bytes 1..12 inclusive |

A Relay that receives a byte `0xA5` followed by a bad checksum drops the packet and
holds its last-known-good state. A misaligned/corrupt byte stream resyncs on the next
`0xA5` it finds.

### Button bit layout (EverLink's own, not any single input API's native layout)

```
0x0001  D-Pad Up        0x0100  Left Shoulder
0x0002  D-Pad Down      0x0200  Right Shoulder
0x0004  D-Pad Left      0x0400  Guide
0x0008  D-Pad Right     0x1000  A
0x0010  Start           0x2000  B
0x0020  Back            0x4000  X
0x0040  Left Thumb      0x8000  Y
0x0080  Right Thumb
```

---

## 3. Rumble (Relay -> Host, v2+)

Whenever the console's rumble command changes, a v2+ Relay sends:

```
RMBL:<left>:<right>\n
```

`left`/`right` are decimal `0-255`. Only sent on change, never repeated for an
unchanged value - Host is responsible for translating this start/stop model into
whatever keep-alive its local rumble API needs (see `SerialLink`'s rumble keep-alive
timer). Not sent at all by v1 firmware.

---

## 4. Kind (v3+)

`kind` is a short, human-readable string identifying **this specific firmware**, not
the individual board/unit. It's what lets someone fork the Relay firmware and have it
show up recognizably in Host's UI, distinct from every other relay, rather than every
relay looking identical apart from a user-assigned nickname.

Examples: `EverLink Relay` (the reference firmware in this repo - deliberately generic
since it speaks more than one protocol depending on its active mode, see section 5),
`Wii U Protocol Relay`, `GameCube Adapter Relay`, `Switch Pro Relay`.

Guidance for firmware authors:
- Keep it short (it's shown inline in a list row) - aim for under 32 characters.
- Name the *protocol/console family* the relay targets, not the board (`ESP32-S3` is
  already reported separately as `chipModel`) and not the specific unit (that's the
  user's nickname).
- No `:` or `,` or `|` (see the reserved-character note above).
- Firmware that doesn't set a `kind` (v1/v2, or a v3 firmware that simply omits the
  field) is shown generically by Host, e.g. as "EverLink Relay".

### Kind as the basis for a wireless advertised name

A firmware with a wireless mode that advertises itself over that transport (BLE name,
Bluetooth Classic device name, etc.) should base that advertised name on `kind`, so the
name a person sees when picking a device to connect to is recognizable the same way
Host's own UI is. Since every unit running the same firmware build shares one `kind`
string, append something unit-unique so multiple relays of the same firmware are still
distinguishable in a scan/picker - the reference firmware uses the last 4 hex
characters of its MAC: `<kind> (<last 4 MAC hex>)`, e.g. `EverLink Relay (BEF0)`.
This is a firmware-side convention, not a protocol requirement - Host does not parse or
validate the wireless advertised name in any way.

## 5. Modes (v3+)

A Relay can support one or more **modes**. Exactly one mode is active at a time. Each
mode is either **wired** or **wireless** - this is what drives Host's UI (a "Pair"
button appears only for a Relay whose *active* mode is wireless; a Relay with more
than one mode gets a mode picker instead).

### Ident reply mode list format

Comma-separated list of modes, each `Name|kind` where `kind` is exactly `wired` or
`wireless`:

```
Wired|wired
Wired (XInput)|wired,Wireless (BLE)|wireless
```

**The list order never changes** - a mode's position in it is the index used by the
mode-switch command, so it has to stay stable. Which mode is active right now is stated
separately by the ident reply's trailing `activeMode` field (section 1), *not* by list
position. (Earlier drafts said "first = active"; that breaks as soon as a Relay can be in
anything but its first mode, e.g. when Host rescans while it's in Wireless.) A Relay with
only one mode listed has no user-facing mode switch at all.

### Mode-switch command (Host -> Relay, v3+)

To ask a Relay to switch to a different mode (by its position in the ident reply's
mode list, zero-indexed), Host sends:

```
0xFD <modeIndex>       (2 bytes: MODE_SWITCH_BYTE, then the index as a raw uint8)
```

The Relay should acknowledge with:

```
MODE:<modeIndex>\n
```

on success (the new active mode's index - normally, but not necessarily, the one just
requested), or simply not reply if the index is out of range / unsupported. Host does
not treat a missing acknowledgement as an error beyond a timeout - see
`SerialLink.TrySwitchMode`.

**Triggering pairing on a wireless mode:** there is no separate "start pairing"
command. A wireless mode's activation (switching to it, including re-activating a mode
that's already active) *is* the pairing trigger. This keeps the wire protocol generic
- Host doesn't need to know anything about *how* a given wireless mode pairs (BLE,
proprietary 2.4GHz, etc.), only that asking a Relay to (re-)enter a wireless mode is
the same action as asking it to pair. A firmware implementing a wireless mode should
treat every activation of that mode (whether it was already active or not) as a fresh
"begin pairing" request. See `RELAY_FORKING_GUIDE.txt` for a worked example.

Firmware that doesn't understand `0xFD` at all (v1/v2, or any firmware that never
reads for it) simply never sees a byte its parser recognizes; as long as a forked
firmware's packet parser only acts on `0xA5` (data) and `0xFE` (ping) as document,
an unrecognized `0xFD` byte lands in whatever byte the firmware's `loop()` happens to
discard next. v3 firmware exposing only one mode does not need to implement `0xFD`
handling at all - Host never sends it to a Relay with a single-entry mode list.

---

## 6. Pairing status (Relay -> Host, v3+, wireless modes only)

Sent by the Relay **unsolicited**, whenever the pairing/connection status of its
*currently active* mode changes - not polled or requested by Host. Only meaningful
for a wireless mode; a Relay whose active mode is wired should never send this line
(and Host ignores it if received while the active mode is wired, treating it as stale -
see below).

```
PAIR:<state>\n
```

| Field | Meaning |
|---|---|
| `state` | Exactly one of `idle`, `searching`, `connected`, `disconnected` - see below. |

There is deliberately no device-name field. Most wireless transports (BLE in
particular - the reference firmware's wireless mode) give a peripheral no reliable way
to learn the friendly name of whatever central connected to it, so this protocol
doesn't pretend otherwise: `connected` means only that *something* is connected, not
*what*. See `RELAY_FORKING_GUIDE.txt` if your transport genuinely can report a real
peer name - that would be a separate, additive field/line, not a retrofit of this one.

### States

- **`idle`** - the wireless mode is active but not currently searching or connected to
  anything. This is also the implicit state immediately after switching TO a wireless
  mode, before pairing has been explicitly triggered - a Relay does not need to send an
  `idle` line just for entering the mode if it starts a search right away instead (see
  below), but should send one if it truly sits idle first.
- **`searching`** - the Relay is actively advertising/scanning for a connection
  (mid-pairing). Sent as soon as pairing begins - see "Triggering pairing" below for
  when that is - and again if/when the Relay reverts to searching after a disconnect,
  if it does so automatically.
- **`connected`** - a wireless connection currently exists, to *something* - see the
  note above on why there's no accompanying identity for it.
- **`disconnected`** - a connection existed and the other side ended it (it disconnected
  or unpaired the Relay). Sent when that happens *unprompted*. A link the Relay dropped
  itself to service a re-pair request is NOT reported this way - that stays `searching`.
  Whether the Relay is still advertising for a reconnect afterwards is up to the firmware
  (the reference firmware's BLE stack is); Host just shows the state.

There is no required minimum interval - send a new `PAIR:` line each time the state
actually changes, and avoid sending repeats of an unchanged state.

### Triggering pairing

As established in section 5: there is no separate "start pairing" command.
(Re-)activating a wireless mode via the mode-switch command (`0xFD`) IS the pairing
trigger - a Relay handling that command for a wireless mode should immediately begin
searching and send `PAIR:searching` right away (not wait for some other signal),
whether or not that mode was already active. This lets Host's Pair button - which just
resends the currently-active mode's index, see `RelayConfigureWindow.PairButton_Click`
- work as "search again" even while already connected to something, e.g. to pair a
different device.

### Host behavior notes

- Host associates the most recent `PAIR:` line with whatever mode was active *at the
  time the line was parsed* (`DeviceInfo.ActiveModeIndex` as last known to Host, via
  `MODE:` acknowledgements - see section 5). A `PAIR:` line that arrives while Host
  believes the active mode is wired is treated as stale/spurious and ignored, rather
  than shown - this can legitimately happen for a brief window right after Host sends a
  mode-switch TO a wired mode but before that switch's own `MODE:` ack has arrived, if a
  last `PAIR:` line from the previous (wireless) mode is still in flight.
- Host has no timeout on `PAIR:` state by itself - a Relay that goes silent after
  `searching` is still shown as `searching` indefinitely on the pairing status/button
  text, independent of (but alongside) the general connection-responsiveness check in
  section 1, which is based on ANY line being received, not specifically `PAIR:` lines.
  If the Relay itself goes fully unresponsive, Host's ordinary "Not responding" handling
  (section 1) is what surfaces that - `PAIR:` state is not re-purposed to detect it.
- **Order matters: send the `MODE:` acknowledgement BEFORE the `PAIR:` line it causes.**
  Because of the staleness rule above, a `PAIR:searching` that reaches Host ahead of the
  `MODE:` ack for the switch that triggered it is discarded (Host still thinks the mode
  is wired). Same-line-order is guaranteed by the serial link, so ack-then-status is safe.
- **A Relay in a wired mode must not advertise or otherwise broadcast on its wireless
  transport**, and should not run wireless-only work at all. The mode list is a promise
  that exactly one mode is live.
- Not sent at all by v1/v2 firmware or by any v3 firmware with no wireless modes -
  Host's pairing UI (see `RelayConfigureWindow`) is only shown/enabled for a Relay whose
  active mode is wireless in the first place, so there's nothing to display it for.

---

## 7. Debug summary line (Relay -> Host, informational only)

Every ~250ms, reference firmware prints a human-readable line for anyone watching with
a serial monitor. Host does not parse or display this - it exists purely for firmware
developers. Format is not part of the protocol and may vary between firmware forks.
