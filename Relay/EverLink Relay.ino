/*
  EverLink Relay - ESP32 Firmware
  ----------------------------------
  Purpose: receives controller input from EverLink Host (the PC app) over a wired serial
  connection and re-emits it as a real USB Xbox 360/XInput controller the console can see
  directly. Requires a native-USB-capable board (S2/S3/P4-family chip) - see
  DeviceInfo.IsUsbCapable on the Host side, which already warns the user about this before
  they get here.

  What it does:
    - Responds to an identification ping (0xFE) with a confirmation + this chip's unique
      MAC address and chip model, so EverLink Host can tell genuine Relay devices apart
      from unrelated COM ports - without needing the board to have been freshly rebooted.
    - Reads the 14-byte packet protocol described in EverLink_Protocol.md over Serial
    - Validates the sync byte and XOR checksum, dropping (and holding last-good-state on)
      anything that fails
    - Mirrors the latest valid state onto a real USB Xbox 360 HID report the instant a
      packet arrives, so console-side input latency is just this one hop
    - Every ~250ms, prints a human-readable summary line over Serial for live debugging
      via any serial monitor (Host does not read or display this text)

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
// Prefix of our reply - Host checks for this exact prefix before trusting the MAC/model
// that follows. Chosen to be extremely unlikely to appear as a false positive from an
// unrelated device that happens to echo random bytes back.
static const char* IDENT_PREFIX = "IAM:EverLink:v1:";

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

  Serial.print(IDENT_PREFIX);
  Serial.print(macStr);
  Serial.print(":");
  Serial.println(ESP.getChipModel());
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
    "[%lums] Btns:%-40s LT:%3u RT:%3u LX:%6d LY:%6d RX:%6d RY:%6d | ok:%lu bad:%lu USBEnumerated:%s\n",
    millis(), pressed.c_str(),
    g_lastState.leftTrigger, g_lastState.rightTrigger,
    g_lastState.leftX, g_lastState.leftY, g_lastState.rightX, g_lastState.rightY,
    g_packetsOk, g_packetsBad,
    everEnumerated ? "yes" : "no"
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

  // Services pending rumble/LED reports from the console so the USB interface stays
  // correctly serviced even though Relay doesn't currently act on their contents.
  ESP32XInput.pollRumble();

  uint32_t now = millis();
  if (now - g_lastSummaryMs >= SUMMARY_INTERVAL_MS) {
    g_lastSummaryMs = now;
    printSummary();
  }
}
