/*
  EverLink Relay - ESP32 Firmware (protocol v2)
  ----------------------------------
  Purpose: receives controller input from EverLink Host (the PC app) over a wired serial
  connection and re-emits it as a real USB Xbox 360/XInput controller the console can see
  directly. Requires a native-USB-capable board (S2/S3/P4-family chip) - see
  DeviceInfo.IsUsbCapable on the Host side, which already warns the user about this before
  they get here.

  What it does:
    - Responds to an identification ping (0xFE) with a confirmation + this firmware's
      protocol version + this chip's unique MAC address and chip model, so EverLink Host
      can tell genuine Relay devices apart from unrelated COM ports (without needing the
      board to have been freshly rebooted) and know which protocol features this specific
      Relay supports.
    - Reads the 14-byte packet protocol described in EverLink_Protocol.md over Serial
    - Validates the sync byte and XOR checksum, dropping (and holding last-good-state on)
      anything that fails
    - Mirrors the latest valid state onto a real USB Xbox 360 HID report the instant a
      packet arrives, so console-side input latency is just this one hop
    - Reads back rumble motor levels the console sends to the emulated pad and forwards
      them to Host as a `RMBL:<left>:<right>` line whenever they change (new in v2 - see
      EverLink_Protocol.md's "Rumble" section), so Host can play the same rumble on the
      real physical controller feeding this Relay
    - Every ~250ms, prints a human-readable summary line over Serial for live debugging
      via any serial monitor (Host does not read or display this text)

  Protocol version: this firmware is v2. v1 firmware sends everything above except the
  rumble line, and its identification reply says "v1" instead of "v2" - Host detects this
  from the ident reply and stays compatible with a v1 Relay, just without rumble support,
  surfaced to the user as a compatibility warning rather than a failure. See
  EverLink_Protocol.md for the exact wire differences.

  Why ping-based identification (not a boot-time announcement): a board that's already
  been running for a while (e.g. left plugged in from an earlier session) wouldn't have
  sent a boot announcement recently, and Host has no way to know if it missed one. A
  ping/response works regardless of how long the board's been powered - Host can ask
  "are you a Relay?" at any time, on demand, during a manual rescan.

  Why the same wire as controller data is fine: Host->Relay and Relay->Host are separate
  physical lines on a UART (full duplex), so there's no collision between outgoing pings/
  packets and incoming responses/debug text - only one PROCESS can hold the COM port,
  which Host satisfies by owning it itself.

  Library: ESP32XInput (provides the USB Xbox 360/XInput HID output itself - .setButton(),
  .setStickLeft(), .send(), etc).

  Note on live USB connection status: earlier revisions of this firmware also emitted a
  dedicated machine-readable "USB:ready"/"USB:not-ready" line, parsed by Host to show a
  live "is the console currently connected" indicator. That was removed - confirmed
  against TinyUSB's own issue tracker (the underlying USB device stack ESP32XInput is
  built on), ESP32XInput.ready() reliably goes TRUE on enumeration but is NOT guaranteed
  to go back to FALSE on a physical unplug for a bus-powered device (one powered off the
  same cable it's signaling over, which is how this board normally runs) - see
  https://github.com/hathach/tinyusb/issues/2478 and
  https://github.com/espressif/esp-usb/issues/38. The underlying USB peripheral has no
  way to tell "cable physically removed" apart from "host went briefly idle" without
  separately monitoring the VBUS power-sense line, which requires an extra resistor
  divider most boards don't have wired up. A status indicator that can silently go stale
  and keep claiming "connected" after the cable's actually been pulled is worse than no
  live indicator at all, so this firmware no longer tries to report it as a trustworthy
  status - if reliable disconnect detection matters for your setup, the real fix is
  wiring VBUS sense into the tinyusb_driver_install() config on a board that exposes
  that pin (see the TinyUSB issues linked above for the exact steps) - out of scope for
  this firmware as written.
*/

#include <Arduino.h>
#include <ESP32XInput.h>

// ---- Identification protocol ----
// Host sends this single byte to ask "are you an EverLink Relay?"
static const uint8_t PING_BYTE = 0xFE;
// This firmware's protocol version - bumped to 2 for rumble support (see
// EverLink_Protocol.md's "Rumble" section). Sent as part of the ident reply so Host can
// tell a v1 Relay (no rumble) from a v2+ one without guessing from behavior.
static const uint8_t PROTOCOL_VERSION = 2;
// Prefix of our reply - Host checks for this exact prefix before trusting the version/
// MAC/model that follows. Chosen to be extremely unlikely to appear as a false positive
// from an unrelated device that happens to echo random bytes back.
static const char* IDENT_PREFIX = "IAM:EverLink:v";

// ---- Protocol constants (must match EverLink_Protocol.md and Host/SerialLink.cs exactly) ----
static const uint8_t SYNC_BYTE = 0xA5;
static const size_t PACKET_SIZE = 14; // sync(1) + buttons(2) + LT(1) + RT(1) + 4x int16(8) + checksum(1)
static const uint32_t BAUD_RATE = 921600;
static const uint32_t SUMMARY_INTERVAL_MS = 250;

// Real Xbox 360 controller VID/PID - what makes the console/PC on the other end recognize
// this as a real Xbox 360 pad rather than a generic HID device.
static const uint16_t XINPUT_VID = 0x045E;
static const uint16_t XINPUT_PID = 0x028E;

// Send USB state at the same cadence as incoming controller packets arrive - the library
// enforces its own minimum interval internally, this just caps how often we ask it to.
static const uint32_t XINPUT_POLL_INTERVAL_MS = 4;

// ESP32XInput is a global object; ESP32XInputClass is its type - the enum lives on the type.
using XButton = ESP32XInputClass::Button;

// ---- Button bit layout ----
// This is EverLink's OWN bit assignment (see EverLink_Protocol.md) - it is not any single
// input API's native layout. Host currently reads controllers via SDL3, not XInput; the
// bit positions below just happen to have originally been chosen to match XInput's layout
// for the buttons XInput could see, plus one extra (Guide) XInput could never expose.
enum ButtonBits : uint16_t {
  BTN_DPAD_UP     = 0x0001,
  BTN_DPAD_DOWN   = 0x0002,
  BTN_DPAD_LEFT   = 0x0004,
  BTN_DPAD_RIGHT  = 0x0008,
  BTN_START       = 0x0010,
  BTN_BACK        = 0x0020,
  BTN_LTHUMB      = 0x0040,
  BTN_RTHUMB      = 0x0080,
  BTN_LSHOULDER   = 0x0100,
  BTN_RSHOULDER   = 0x0200,
  BTN_GUIDE       = 0x0400, // only reachable now that Host reads via SDL, not XInput
  BTN_A           = 0x1000,
  BTN_B           = 0x2000,
  BTN_X           = 0x4000,
  BTN_Y           = 0x8000,
};

struct ControllerState {
  uint16_t buttons;
  uint8_t leftTrigger;
  uint8_t rightTrigger;
  int16_t leftX, leftY;
  int16_t rightX, rightY;
};

static ControllerState g_lastState = {};
static uint32_t g_packetsOk = 0;
static uint32_t g_packetsBad = 0;
static uint32_t g_lastSummaryMs = 0;

// Latest rumble motor levels, set by onRumbleReceived() (registered as ESP32XInput's
// rumble callback in setup() - see below) and read back by sendRumbleIfChanged() in
// loop(). This firmware previously tried ESP32XInput.getLastRumbleLeft()/
// getLastRumbleRight() as if pollRumble() populated some queryable "last known state" -
// that was wrong. The library's actual API (per its own documentation) is
// callback-based: ESP32XInput.onRumble(callback) registers a function the library calls
// itself, from inside pollRumble(), whenever the console's rumble command actually
// changes - there is no separate getter to poll instead. volatile because this is written
// from the callback (invoked during pollRumble(), itself called from loop()) and read
// from sendRumbleIfChanged() (also called from loop()) - both on the same thread in this
// firmware's structure, so volatile here is a defensive habit rather than a strict
// requirement, but costs nothing and protects against a future change that isn't.
static volatile uint8_t g_rumbleLeft = 0;
static volatile uint8_t g_rumbleRight = 0;
static volatile bool g_rumbleChangedSinceSent = false;

// Registered with ESP32XInput.onRumble() in setup(). Called by the library itself
// (from within pollRumble(), per its documented "invokes user callbacks on state
// change" behavior) whenever the console's rumble command changes - NOT called on a
// timer or every poll, only on an actual change, so g_rumbleChangedSinceSent here plays
// the same "only send when different" role sendRumbleIfChanged() used to handle itself
// by comparing against a remembered last-sent value. Keeping that comparison here too
// (implicitly, by simply always marking changed on every callback invocation) rather
// than re-deriving it from getters that don't exist in this library.
void onRumbleReceived(uint8_t left, uint8_t right) {
  g_rumbleLeft = left;
  g_rumbleRight = right;
  g_rumbleChangedSinceSent = true;
}

// Sends our identification reply, including this chip's factory-burned unique MAC address
// and its chip model (e.g. "ESP32", "ESP32-S3") so Host can warn if this specific
// board lacks the native USB peripheral needed for the HID/console-facing role.
// getEfuseMac() returns a 48-bit value from hardware - guaranteed unique per chip, present
// even on totally blank/first-boot chips, stable across reflashing. getChipModel() reads
// the same chip-identity info esptool's chip_id command shows, just from within firmware.
void sendIdentReply() {
  uint64_t mac = ESP.getEfuseMac();
  char macStr[13]; // 12 hex chars + null terminator
  snprintf(macStr, sizeof(macStr), "%012llX", mac);

  // Serial.print(PROTOCOL_VERSION) would hit the exact same uint8_t-prints-as-a-raw-byte
  // trap documented in sendRumbleIfChanged() below - PROTOCOL_VERSION is a uint8_t, so
  // that call would send the single byte 0x02, not the ASCII character '2', silently
  // corrupting every ident reply this firmware ever sends (Host's TryPingDevice parses
  // this field with int.TryParse, which would simply fail on a non-digit byte - the
  // Relay would never even be recognized as a valid EverLink device). printf's %u forces
  // decimal-digit output regardless of argument width, same fix applied there.
  Serial.printf("%s%u:%s:%s\n", IDENT_PREFIX, PROTOCOL_VERSION, macStr, ESP.getChipModel());
}

// Checks for and handles a pending identification ping. Returns true if one was handled
// (caller should not also try to parse a data packet this iteration, since the ping byte
// was consumed here rather than left for the packet parser).
bool tryHandlePing() {
  if (Serial.available() < 1) return false;
  if (Serial.peek() != PING_BYTE) return false;

  Serial.read(); // consume the ping byte
  sendIdentReply();
  return true;
}

// Reads and validates one packet if a full one is available. Returns true if g_lastState
// was updated. Non-blocking - call this every loop() iteration.
bool tryReadPacket() {
  // Need at least a full packet's worth of bytes buffered before attempting a parse.
  if (Serial.available() < (int)PACKET_SIZE) return false;

  // Look for sync byte without necessarily being aligned yet - if the first byte isn't
  // 0xA5, discard it and try again next call. This lets us resync after any corruption.
  if (Serial.peek() != SYNC_BYTE) {
    Serial.read(); // discard misaligned byte
    return false;
  }

  uint8_t buf[PACKET_SIZE];
  size_t n = Serial.readBytes(buf, PACKET_SIZE);
  if (n != PACKET_SIZE) return false; // shouldn't happen given the availability check above

  // Verify checksum: XOR of bytes[1..12] should equal buf[13]
  uint8_t checksum = 0;
  for (size_t i = 1; i < PACKET_SIZE - 1; i++) checksum ^= buf[i];

  if (checksum != buf[PACKET_SIZE - 1]) {
    g_packetsBad++;
    return false; // drop it - g_lastState intentionally left unchanged (hold last good state)
  }

  ControllerState s;
  s.buttons      = (uint16_t)(buf[1] | (buf[2] << 8));
  s.leftTrigger  = buf[3];
  s.rightTrigger = buf[4];
  s.leftX        = (int16_t)(buf[5]  | (buf[6]  << 8));
  s.leftY        = (int16_t)(buf[7]  | (buf[8]  << 8));
  s.rightX       = (int16_t)(buf[9]  | (buf[10] << 8));
  s.rightY       = (int16_t)(buf[11] | (buf[12] << 8));

  g_lastState = s;
  g_packetsOk++;
  return true;
}

// ---- USB XInput output ----

// Translates the current g_lastState into a USB Xbox 360 HID report and sends it. Called
// once per drained batch of incoming serial packets (see loop()), not on every single
// packet, since only the newest state matters for what actually reaches the console.
void updateUsbState() {
  const uint16_t buttons = g_lastState.buttons;

  // D-pad: ESP32XInput takes a single combined hat value rather than 4 independent
  // button bits (matches the real Xbox 360 HID report layout, which encodes the d-pad
  // as one 4-bit direction rather than 4 separate buttons).
  //   0=Up 1=Up+Right 2=Right 3=Down+Right 4=Down 5=Down+Left 6=Left 7=Up+Left 8=released
  const bool up    = (buttons & BTN_DPAD_UP) != 0;
  const bool down  = (buttons & BTN_DPAD_DOWN) != 0;
  const bool left  = (buttons & BTN_DPAD_LEFT) != 0;
  const bool right = (buttons & BTN_DPAD_RIGHT) != 0;

  uint8_t hat = 8; // released - also the fallback for impossible opposing combos (up+down, left+right both held)
  if (up && !down) {
    if (right && !left) hat = 1;
    else if (left && !right) hat = 7;
    else if (!left && !right) hat = 0;
  } else if (down && !up) {
    if (right && !left) hat = 3;
    else if (left && !right) hat = 5;
    else if (!left && !right) hat = 4;
  } else if (!up && !down) {
    if (right && !left) hat = 2;
    else if (left && !right) hat = 6;
  }
  ESP32XInput.setHat(hat);

  ESP32XInput.setButton(XButton::START, (buttons & BTN_START) != 0);
  ESP32XInput.setButton(XButton::BACK, (buttons & BTN_BACK) != 0);
  ESP32XInput.setButton(XButton::XBOX, (buttons & BTN_GUIDE) != 0);
  ESP32XInput.setButton(XButton::LEFT_THUMB, (buttons & BTN_LTHUMB) != 0);
  ESP32XInput.setButton(XButton::RIGHT_THUMB, (buttons & BTN_RTHUMB) != 0);
  ESP32XInput.setButton(XButton::LEFT_SHOULDER, (buttons & BTN_LSHOULDER) != 0);
  ESP32XInput.setButton(XButton::RIGHT_SHOULDER, (buttons & BTN_RSHOULDER) != 0);
  ESP32XInput.setButton(XButton::A, (buttons & BTN_A) != 0);
  ESP32XInput.setButton(XButton::B, (buttons & BTN_B) != 0);
  ESP32XInput.setButton(XButton::X, (buttons & BTN_X) != 0);
  ESP32XInput.setButton(XButton::Y, (buttons & BTN_Y) != 0);

  // Triggers: EverLink wire format is 0..255 (matches classic XInput); ESP32XInput wants
  // 0..32768. Scaled here rather than changing the wire protocol, which stays fixed so
  // Host doesn't need to know or care what any particular Relay's output library expects.
  uint16_t leftTrigger  = (uint16_t)(((uint32_t)g_lastState.leftTrigger  * 32768UL) / 255UL);
  uint16_t rightTrigger = (uint16_t)(((uint32_t)g_lastState.rightTrigger * 32768UL) / 255UL);
  ESP32XInput.setLeftTrigger(leftTrigger);
  ESP32XInput.setRightTrigger(rightTrigger);

  // Sticks: EverLink already uses signed int16, matching XInput's native range exactly -
  // no rescaling needed.
  ESP32XInput.setStickLeft(g_lastState.leftX, g_lastState.leftY);
  ESP32XInput.setStickRight(g_lastState.rightX, g_lastState.rightY);

  ESP32XInput.send();
}

// ---- Rumble reporting (Relay -> Host, v2+) ----

// Reads the rumble motor levels the console has told the emulated pad to play (serviced
// by ESP32XInput.pollRumble() in loop(), which must run first) and, if either level has
// changed since the last report, sends a `RMBL:<left>:<right>` line to Host - see
// EverLink_Protocol.md's "Rumble" section. Cheap enough to call every loop() iteration:
// this only compares two bytes and does nothing further unless they differ.
// Forwards the latest rumble state to Host as a `RMBL:<left>:<right>` line - see
// EverLink_Protocol.md's "Rumble" section. Only sends when onRumbleReceived() has set
// g_rumbleChangedSinceSent, i.e. only after ESP32XInput's own callback has actually
// fired with a changed value - this firmware doesn't do its own change-detection
// against a remembered previous value anymore (there's nothing to poll/compare against
// between callback firings), it just trusts the library's "callback only fires on
// change" behavior and forwards whatever the callback most recently reported, once.
void sendRumbleIfChanged() {
  if (!g_rumbleChangedSinceSent) return;

  // Snapshot before clearing the flag - onRumbleReceived() could in principle fire again
  // between these two lines (it's called from pollRumble(), not from an interrupt, so in
  // this firmware's single-threaded loop() structure it actually can't during this
  // window - but reading into locals first costs nothing and avoids relying on that).
  uint8_t left = g_rumbleLeft;
  uint8_t right = g_rumbleRight;
  g_rumbleChangedSinceSent = false;

  // IMPORTANT: Serial.print(uint8_t) does NOT print decimal digits - uint8_t overloads
  // resolve the same as char/byte, so Serial.print(left) would send the raw 8-bit VALUE
  // as a single byte (e.g. the byte 0xB4 for 180), not the three ASCII characters "1",
  // "8", "0". printf's %u format specifier forces decimal-digit output regardless of the
  // argument's underlying width, sidestepping that trap entirely - same fix as
  // printSummary() already uses for LT/RT (also uint8_t) further down in this file, and
  // as sendIdentReply() above now also uses for PROTOCOL_VERSION.
  Serial.printf("RMBL:%u:%u\n", left, right);
}

// ---- Debug output ----

void appendIfPressed(String &out, uint16_t buttons, uint16_t bit, const char *name) {
  if (buttons & bit) {
    if (out.length() > 0) out += ",";
    out += name;
  }
}

void printSummary() {
  String pressed;
  appendIfPressed(pressed, g_lastState.buttons, BTN_A, "A");
  appendIfPressed(pressed, g_lastState.buttons, BTN_B, "B");
  appendIfPressed(pressed, g_lastState.buttons, BTN_X, "X");
  appendIfPressed(pressed, g_lastState.buttons, BTN_Y, "Y");
  appendIfPressed(pressed, g_lastState.buttons, BTN_START, "Start");
  appendIfPressed(pressed, g_lastState.buttons, BTN_BACK, "Back");
  appendIfPressed(pressed, g_lastState.buttons, BTN_GUIDE, "Guide");
  appendIfPressed(pressed, g_lastState.buttons, BTN_LSHOULDER, "LB");
  appendIfPressed(pressed, g_lastState.buttons, BTN_RSHOULDER, "RB");
  appendIfPressed(pressed, g_lastState.buttons, BTN_LTHUMB, "LThumb");
  appendIfPressed(pressed, g_lastState.buttons, BTN_RTHUMB, "RThumb");
  appendIfPressed(pressed, g_lastState.buttons, BTN_DPAD_UP, "DUp");
  appendIfPressed(pressed, g_lastState.buttons, BTN_DPAD_DOWN, "DDown");
  appendIfPressed(pressed, g_lastState.buttons, BTN_DPAD_LEFT, "DLeft");
  appendIfPressed(pressed, g_lastState.buttons, BTN_DPAD_RIGHT, "DRight");
  if (pressed.length() == 0) pressed = "-";

  // ESP32XInput.ready() reflects whether this board has been enumerated as an Xbox 360
  // controller by whatever's plugged into its USB port - included here as raw debug
  // telemetry only, not a trustworthy live connection indicator (see this file's top
  // comment for why: it doesn't reliably clear on a physical unplug). Host does not
  // parse or display this value; it's just part of the human-readable summary line, for
  // whoever happens to be watching this board's serial output with that caveat in mind.
  bool everEnumerated = ESP32XInput.ready();

  Serial.printf(
    "[%lums] Btns:%-40s LT:%3u RT:%3u LX:%6d LY:%6d RX:%6d RY:%6d | ok:%lu bad:%lu USBEnumerated:%s RumbleL:%3u RumbleR:%3u\n",
    millis(), pressed.c_str(),
    g_lastState.leftTrigger, g_lastState.rightTrigger,
    g_lastState.leftX, g_lastState.leftY, g_lastState.rightX, g_lastState.rightY,
    g_packetsOk, g_packetsBad,
    everEnumerated ? "yes" : "no",
    g_rumbleLeft, g_rumbleRight
  );
}

void setup() {
  Serial.begin(BAUD_RATE);
  // Give the host side a moment before we start printing, purely cosmetic for the monitor.
  delay(200);
  Serial.println("=== EverLink Relay ===");
  Serial.printf("Waiting for packets at %lu baud...\n", BAUD_RATE);

  ESP32XInput.begin(XINPUT_VID, XINPUT_PID);
  ESP32XInput.setPollInterval(XINPUT_POLL_INTERVAL_MS); // 4ms = 250Hz, matching the EverLink packet rate
  ESP32XInput.releaseAll(); // start from a fully-released controller state, not whatever garbage memory held before

  // Registers onRumbleReceived() as the function ESP32XInput calls whenever the console's
  // rumble command changes - this is the library's actual rumble API (see
  // onRumbleReceived's doc comment above for why the previous getLastRumbleLeft()/
  // getLastRumbleRight()-based approach was wrong). Must be registered before pollRumble()
  // is ever called in loop() for the very first rumble command to be caught, though in
  // practice a console typically doesn't send one until well after enumeration, so this
  // ordering only matters for correctness/clarity, not to avoid a real race in this
  // firmware's startup sequence.
  ESP32XInput.onRumble(onRumbleReceived);

  Serial.println("USB XInput controller initialized.");
}

void loop() {
  // Check for an identification ping first, each iteration - it's a single distinct byte
  // (0xFE) that would otherwise confuse the packet parser if left for it to find. Real
  // packets always start with 0xA5, so there's no ambiguity between the two.
  tryHandlePing();

  // Drain as many complete packets as are currently available each loop iteration -
  // keeps g_lastState fresh even if loop() gets called less often than packets arrive.
  bool gotPacket = false;
  while (tryReadPacket()) {
    gotPacket = true;
  }

  // Only push to USB once per drained batch - the newest state is all that matters for
  // what the console actually sees, no point re-sending for every intermediate packet.
  if (gotPacket) {
    updateUsbState();
  }

  // Services pending rumble/LED OUT-endpoint packets from the console, draining and
  // dispatching them - this is what actually invokes onRumbleReceived() (registered in
  // setup()) whenever the console's rumble command changes. Nothing is read back from
  // ESP32XInput after this call; onRumbleReceived() already wrote whatever changed into
  // g_rumbleLeft/g_rumbleRight/g_rumbleChangedSinceSent as a side effect of this call.
  ESP32XInput.pollRumble();

  // Forward the rumble state to Host, but only if onRumbleReceived() actually set
  // g_rumbleChangedSinceSent during the pollRumble() call just above (new in v2 - see
  // EverLink_Protocol.md's "Rumble" section). Must run after pollRumble(): that's the
  // only place g_rumbleChangedSinceSent ever gets set.
  sendRumbleIfChanged();

  uint32_t now = millis();
  if (now - g_lastSummaryMs >= SUMMARY_INTERVAL_MS) {
    g_lastSummaryMs = now;
    printSummary();
  }
}
