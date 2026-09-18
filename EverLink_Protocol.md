# EverLink Protocol: Host &harr; Relay

This is the wire protocol spoken between **EverLink Host** (the Windows PC app) and
**EverLink Relay** (the ESP32 firmware) over a wired USB-serial connection.

## Identification ping

Before treating any COM port as an EverLink Relay, Host sends a single byte:

```
0xFE
```

A genuine Relay replies with a text line:

```
IAM:EverLink:v<N>:<12 hex chars>:<chip model>
```

where `<N>` is the Relay firmware's protocol version (currently `2`), `<12 hex chars>`
is the chip's factory-burned unique MAC address (from `ESP.getEfuseMac()`), and
`<chip model>` is the string from `ESP.getChipModel()` (e.g. `ESP32`, `ESP32-S3`,
`ESP32-S2`). Example:

```
IAM:EverLink:v2:B0CBD8CCBEF0:ESP32
```

Host stays compatible with a `v1` Relay (an older firmware build still on the wire):
the 14-byte controller packet, identification ping, and sync/checksum are unchanged
between v1 and v2, and Host continues to talk to a v1 Relay normally. What v1 lacks
is the rumble line below - Host detects the version from the ident reply and simply
never sends/expects `RMBL:` traffic to/from a v1 Relay. This is surfaced to the user
as a compatibility warning (see "Feature compatibility" below) rather than a hard
failure, since everything else still works.

The MAC is used as the stable device identity for user-assigned nicknames - NOT the COM
port name, since COM port assignment can change if a board is moved to a different USB
port. The chip model is used to warn the user if a specific board lacks the native USB
peripheral required to output as a USB HID gamepad the console can see (only S2/S3/P4-family
chips have it - plain ESP32/C3 do not). The same firmware binary is flashed to every board;
no per-device firmware editing is needed since both the identity and capability info come
from hardware/SDK calls, not from anything written into the code per-device.

Unrelated devices (a mouse dongle, a modem, anything not running EverLink Relay firmware)
will not recognize `0xFE` as anything meaningful and won't reply with the expected prefix -
Host treats "no valid reply within the timeout" as "not a Relay" and excludes that port
from the device list entirely.

Why ping-based (not a boot-time announcement): a board that's been powered on for a while
wouldn't have announced anything recently, and Host has no way to know if it missed a
boot-time message. Pinging on demand (during a manual rescan) works regardless of how long
the board's been running.

## Controller data packets

Binary, little-endian, fixed-size 14-byte packets, sent continuously at ~250Hz (every 4ms)
over a serial connection at 921600 baud.

## Packet layout

| Offset | Size | Field          | Notes                                   |
|--------|------|----------------|------------------------------------------|
| 0      | 1    | Sync byte      | Always `0xA5`                            |
| 1-2    | 2    | Buttons        | uint16, see "Button bit layout" below    |
| 3      | 1    | Left trigger   | uint8, 0-255                             |
| 4      | 1    | Right trigger  | uint8, 0-255                             |
| 5-6    | 2    | Left stick X   | int16, -32768..32767                     |
| 7-8    | 2    | Left stick Y   | int16, -32768..32767                     |
| 9-10   | 2    | Right stick X  | int16                                    |
| 11-12  | 2    | Right stick Y  | int16                                    |
| 13     | 1    | Checksum       | XOR of bytes 1-12                        |

Total: 14 bytes/packet.

## Button bit layout

This is EverLink's own bit assignment - not a direct copy of any single input API's native
layout, though most bit positions happen to line up with XInput's `wButtons` for historical
reasons (that's what Host originally read controllers through). Host now reads controllers
via SDL3 instead, which is what let this protocol add the Guide button (`0x0400`) - XInput
could never expose that button to applications at all, at the OS level, regardless of what
Host's code did.

```
0x0001  DPad Up
0x0002  DPad Down
0x0004  DPad Left
0x0008  DPad Right
0x0010  Start
0x0020  Back
0x0040  Left Thumb (stick click)
0x0080  Right Thumb (stick click)
0x0100  Left Shoulder (bumper)
0x0200  Right Shoulder (bumper)
0x0400  Guide (Xbox/PS/Home button)
0x1000  A
0x2000  B
0x4000  X
0x8000  Y
```

## Rumble (Relay -> Host, v2+)

The console can ask the emulated Xbox 360 pad to rumble (e.g. a game's force-feedback
event); Relay receives this via `ESP32XInput.onRumble(callback)` - a callback
registered once in `setup()`, which the library invokes itself (from inside
`ESP32XInput.pollRumble()`, called every `loop()` iteration) whenever the console's
rumble command actually changes - and forwards the two motor levels to Host as a plain
text line, so Host can, in turn, play the same rumble on the real physical controller
feeding that Relay:

```
RMBL:<left>:<right>
```

where `<left>` and `<right>` are each the 8-bit (0-255) motor strength the callback
receives - matching the same 0-255 scale the wire protocol already uses for triggers,
so no separate scaling note is needed here (Host rescales these up to SDL's 16-bit
rumble range on the way out, the mirror image of what it already does for trigger axes
on the way in - see `Host/SdlController.cs`). Example:

```
RMBL:180:96
```

Sent only when the motor levels actually change, since the library's callback itself
only fires on a change - there's no separate polling/comparison happening on Relay's
side, it's simply forwarding each callback invocation as one line. This is why it's a
simple text line alongside the existing debug summary rather than a new fixed-size binary
frame like the controller packet: it's low-rate and doesn't need to survive
misalignment/resync the way the continuous 250Hz stream does.

A v1 Relay never sends this line at all (its firmware doesn't read rumble); Host
does not wait for it or treat its absence as an error - it's purely additive. A v1
Relay also never receives rumble commands from Host, since there's nothing on Host's
side to send there either (Host only ever reacts to what Relay reports, it doesn't
originate rumble commands) - the whole point of this feature is games on the console
side, via Relay's USB HID connection, driving rumble back to the player's real pad,
not the reverse.

## Why this shape

The axis ranges (signed 16-bit sticks, 0-255 triggers) were chosen to match XInput's native
ranges, since that made the original Host implementation a direct field-for-field copy with
no rescaling. Host has since switched to reading controllers via SDL3, whose native ranges
differ slightly (SDL reports triggers as 0-32767, not 0-255) - `Host/SdlController.cs`
rescales triggers on the way in so this wire format didn't need to change. The button bits
are Host's own assignment, decoupled from any specific input API's layout, so future input
sources can be added without altering the protocol again.

The USB-output half of Relay firmware unpacks this same struct and copies the values into
a USB HID gamepad report that mimics an Xbox 360 controller's report layout (VID 0x045E /
PID 0x028E) - an identity confirmed present in `gamecontrollerdb.txt` and already
known-compatible with the Evercade VS-R's SDL-based input handling. See `updateUsbState()`
in `Relay/EverLink Relay.ino`.

## Checksum

Simple XOR over bytes 1-12 (everything between the sync byte and the checksum itself).
Not cryptographic - just enough to let Relay detect a corrupted or misaligned packet on a
wired link and drop it rather than acting on garbage input. On mismatch, firmware discards
the packet and resyncs on the next `0xA5` byte it sees.

## Rate

Sent every 4ms (~250Hz) from Host. This is deliberately higher than either end can actually
make use of - USB polling intervals (1-8ms typical) are the real latency floor on both the
Host-to-Relay leg and the Relay-to-console leg. The margin just means Host is never
queuing/waiting; it costs almost nothing at these packet sizes and baud rate.

## USB status line (Relay -> Host)

Removed as a parsed protocol element. An earlier revision had Relay firmware print a
dedicated `USB:ready`/`USB:not-ready` line, which Host parsed to drive a live "USB Output:
Ready/Not Connected" status per-Relay. That was removed after confirming the underlying
signal (`ESP32XInput.ready()`, built on TinyUSB) can't reliably detect a physical unplug
for a bus-powered board without extra VBUS-sense hardware most boards don't have wired up
(see `Relay/EverLink Relay.ino`'s top comment, and
https://github.com/hathach/tinyusb/issues/2478 / https://github.com/espressif/esp-usb/issues/38
for the underlying TinyUSB limitation). A status indicator that can silently go stale and
keep claiming "connected" after the cable's actually been pulled was judged worse than no
live indicator at all.

What Host still shows is `DeviceInfo.IsUsbCapable` only - a fixed hardware fact (does this
board's chip model support native USB at all), read once from the identification ping's
chip model string, not a live connection state. See `Host/MainWindow.xaml.cs`'s
`UsbCapabilityLabel`.

Firmware's human-readable debug summary (see Rate section above) still includes an
`USBEnumerated:yes`/`USBEnumerated:no` field from the same underlying `ESP32XInput.ready()`
call, visible to anyone watching the board's serial output directly (e.g. Arduino IDE's
Serial Monitor) - but it is not parsed or displayed by Host, and carries the same "doesn't
reliably clear on unplug" caveat if you're reading it directly.

## Resolved: checksum-failure behavior

Settled as drop-and-hold-last-state (not drop-and-resync-to-zero): on a checksum mismatch,
firmware discards the packet and keeps whatever `g_lastState` it last validated, rather than
zeroing out. This avoids a visible input glitch (e.g. a stick snapping to center) from an
occasional flipped bit on an otherwise-healthy link. See `tryReadPacket()` in
`Relay/EverLink Relay.ino`.
