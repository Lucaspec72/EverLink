/*
  EverLink Relay - ESP32 Firmware (protocol v3)
  ---------------------------------------------
  Reference/default firmware: the stock relay this repo ships, with two modes - Wired
  (XInput) over USB and Wireless (BLE). See RELAY_FORKING_GUIDE.txt if you're building a
  variant (different Kind string, different Modes, a different console protocol).

  Purpose: receives controller input from EverLink Host (the PC app) over serial and
  re-emits it as either:
    - Wired (XInput): a real USB Xbox 360/XInput HID controller. Needs a native-USB-capable
      board (S2/S3/P4-family); Host already warns about this (DeviceInfo.IsUsbCapable).
    - Wireless (BLE): a generic Bluetooth LE HID gamepad for a PC or phone. Works on any
      BLE-capable ESP32. NOT an "Xbox controller" over the air and NOT reachable by an
      actual Xbox console (see BLE_GAMEPAD_LIBRARY_NOTE / BLE_XBOX_CONSOLE_NOTE).
  Exactly one mode is active at a time (g_activeModeIndex, EverLink_Protocol.md section 5).

  What it does:
    - Answers an identification ping (0xFE) with protocol version, chip MAC + model, Kind,
      Mode list and the active mode index, so Host can recognise a Relay at any time
      (not just right after boot - hence ping/reply rather than a boot announcement).
    - Reads the 14-byte packet protocol (EverLink_Protocol.md) over Serial, validating the
      sync byte and XOR checksum; bad packets are dropped and the last good state held.
    - Mirrors the latest valid state onto the active transport's HID report immediately.
    - Wired mode: forwards console rumble to Host as `RMBL:<left>:<right>` (v2+). Wireless
      mode has no rumble - the BLE gamepad library exposes no received-rumble callback.
    - Handles mode-switch requests (0xFD <index>, v3+).
    - Wireless mode: reports connection state to Host as `PAIR:<state>` lines (v3+,
      section 6), state only - see BLE_NO_PEER_NAME_NOTE.
    - Every ~250ms prints a human-readable summary line (debug only; Host ignores it).
  Older relays: v1 lacks rumble and Kind/Modes; v2 adds rumble. Host detects the version
  from the ident reply and degrades gracefully (see EverLink_Protocol.md).

  Serial is full duplex, so replies/debug text never collide with incoming packets; only
  one PROCESS may hold the COM port, which Host satisfies by owning it.

  Libraries:
    - ESP32XInput: USB Xbox 360/XInput HID output (Wired mode).
    - BleGamepad (lemmingDev/ESP32-BLE-Gamepad): BLE HID gamepad output (Wireless mode).

  BLE_GAMEPAD_LIBRARY_NOTE - why generic BLE HID, not "BLE XInput":
  The library's XInput emulation (Xbox One S/Series X descriptor over BLE) only exists on an
  unreleased feature branch (GitHub issue #346, "v0.8.0-rc0", marked "hold off on merging
  and releasing"). A relay firmware shouldn't depend on that, so this uses the stable
  generic-BLE-HID mode. PCs/phones see a normal Bluetooth gamepad, fully functional for
  input. If real BLE XInput ships in a stable release, wiring it in is a natural follow-up.

  BLE_ALWAYS_RESIDENT_NOTE - why BLE starts lazily and is never torn down:
  The stable library can't reliably stop/restart its BLE server (issue #314: end() does not
  stop the server, stop advertising or disconnect the client, and the library re-advertises
  by itself after a disconnect). So:
    - BleGamepad is created and begin()'d the first time Wireless mode is activated
      (ensureBleStarted()), NOT at boot - a Wired-only Relay never starts Bluetooth.
    - Once started it stays resident. Leaving Wireless mode disconnects any peer and stops
      advertising (endWirelessMode()); while Wired is active, enforceBleOffWhileWired()
      repeats that because the library would otherwise quietly re-advertise.
    - Returning to Wireless just restarts advertising (after force-disconnecting any
      existing peer on a re-pair request).
  What can't be done: free the BLE stack's memory/radio init once used.

  BLE_XBOX_CONSOLE_NOTE - why this can never reach an actual Xbox console:
  Xbox consoles have no Bluetooth radio; they need Microsoft's proprietary non-BLE "Xbox
  Wireless" 2.4GHz protocol. No BLE firmware can appear as a controller to one. Wireless
  mode is therefore PC/phone-only (EverLink_Protocol.md section 6, RELAY_FORKING_GUIDE.txt).

  BLE_NO_PEER_NAME_NOTE - why PAIR:connected carries no device identity:
  The Relay is the BLE peripheral; the PC/phone is the central, and a peripheral has no
  standard way to learn a central's friendly name (NimBLEConnInfo exposes address and
  encryption state only). Rather than dress a raw address up as a name, PAIR: lines report
  state only: idle / searching / connected / disconnected.

  BLE_MAPPING_NOTE - see the comment above the mapping block below.

  Live USB connection status was deliberately removed: ESP32XInput.ready() goes TRUE on
  enumeration but isn't guaranteed to go FALSE on a physical unplug for a bus-powered device
  (https://github.com/hathach/tinyusb/issues/2478, https://github.com/espressif/esp-usb/issues/38).
  A status indicator that can go stale is worse than none; reliable detection needs VBUS
  sense wired into tinyusb_driver_install() on a board that exposes it - out of scope here.
  BLE connection state has no such problem (a BLE disconnect is a real link-layer event),
  which is why PAIR: exists for wireless only.
*/

#include <Arduino.h>
#include <ESP32XInput.h>
#include <BleGamepad.h>
// Included explicitly for NimBLEDevice::getServer(), NimBLEAdvertising and
// BLE_ERR_REM_USER_CONN_TERM, used to control advertising and force-disconnect a peer
// (see BLE_ALWAYS_RESIDENT_NOTE).
#include <NimBLEDevice.h>

// Must stay above every function: the Arduino IDE inserts auto-generated prototypes
// before any of this file's own code, so a function taking a PairState would otherwise
// fail to compile ("PairState was not declared in this scope"). For the same reason, no
// function in this file takes any OTHER custom type as a parameter.
enum class PairState : uint8_t { Idle, Searching, Connected, Disconnected };

// ---- Identification / command protocol (EverLink_Protocol.md sections 4-6) ----
static const uint8_t PING_BYTE        = 0xFE;  // Host: "are you an EverLink Relay?"
static const uint8_t MODE_SWITCH_BYTE = 0xFD;  // Host: followed by one byte = RELAY_MODES index
static const uint8_t SYNC_BYTE        = 0xA5;  // starts every 14-byte data packet
static const uint8_t PROTOCOL_VERSION = 3;
// Host checks this exact prefix before trusting the rest of the reply.
static const char* IDENT_PREFIX = "IAM:EverLink:v";

// Kind identifies THIS FIRMWARE (not the unit - that's the user's nickname in Host). Kept
// generic since the firmware speaks more than one protocol. Forks should change it. Must
// not contain ':', ',' or '|' (reserved by the wire format).
static const char* RELAY_KIND = "EverLink Relay";
// "Name|id" pairs, comma separated. Mode names spell out the real protocol so Wired isn't
// mistaken for "just serial" nor Wireless for real Xbox Wireless. The index order must
// match MODE_WIRED/MODE_WIRELESS below and stays fixed (indices are used by 0xFD).
static const char* RELAY_MODES = "Wired (XInput)|wired,Wireless (BLE)|wireless";
static const uint8_t MODE_WIRED    = 0;
static const uint8_t MODE_WIRELESS = 1;
static const uint8_t MODE_COUNT    = 2;

// Starts Wired: the mode existing setups expect, with no pairing step before it's usable.
static uint8_t g_activeModeIndex = MODE_WIRED;

// ---- Constants ----
static const size_t PACKET_SIZE = 14; // sync(1) + buttons(2) + LT(1) + RT(1) + 4x int16(8) + checksum(1)
static const unsigned long BAUD_RATE = 921600;
static const uint32_t SUMMARY_INTERVAL_MS = 250;

// Real Xbox 360 controller VID/PID, so the other end binds its native Xbox 360 driver.
static const uint16_t XINPUT_VID = 0x045E;
static const uint16_t XINPUT_PID = 0x028E;
static const uint32_t XINPUT_POLL_INTERVAL_MS = 4; // 250Hz, matching the packet rate

// The enum lives on the library's type, not on the global ESP32XInput object.
using XButton = ESP32XInputClass::Button;

// Wireless (BLE) mode - see BLE_GAMEPAD_LIBRARY_NOTE.
static const char* BLE_MANUFACTURER = "EverLink";
// Reported as the BLE "Model Number"; receivers that build a name from manufacturer +
// product (SDL) show "EverLink Controller" instead of "EverLink 1.0.0". Must be static:
// the library keeps the pointer.
static const char* BLE_MODEL_NUMBER = "Controller";
static const uint8_t BLE_INITIAL_BATTERY = 100; // no real battery; a static 100 is the usual choice
// Cap on BLE HID reports (~125 Hz). Host streams at 250 Hz but BLE connection intervals
// are typically 7.5-30 ms; flooding makes the stack drop/queue reports, which shows up as
// flicker. Only the latest state matters, so skipping intermediates loses nothing.
static const uint32_t BLE_REPORT_MIN_INTERVAL_MS = 8;
static const uint32_t BLE_WATCHDOG_INTERVAL_MS = 100;       // wired-mode "is BLE silent?" recheck
static const uint32_t REPAIR_DISCONNECT_TIMEOUT_MS = 2000;  // stop waiting for a forced disconnect after this

// ---- Button bit layout ----
// EverLink's OWN bit assignment (EverLink_Protocol.md), not any single input API's layout.
// Bits happen to match XInput's for the buttons XInput could see, plus Guide (reachable
// now that Host reads via SDL3).
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
  BTN_GUIDE       = 0x0400,
  BTN_A           = 0x1000,
  BTN_B           = 0x2000,
  BTN_X           = 0x4000,
  BTN_Y           = 0x8000,
};

// One row per non-d-pad button: its EverLink bit, USB (XInput) button, BLE HID button
// slot, and debug-summary name. The BLE slots are NOT in order - see BLE_MAPPING_NOTE.
struct ButtonMap {
  uint16_t bit;
  XButton usb;
  uint8_t ble;
  const char* name;
};
static const ButtonMap BUTTON_MAP[] = {
  { BTN_A,         XButton::A,              BUTTON_1,  "A"      },
  { BTN_B,         XButton::B,              BUTTON_2,  "B"      },
  { BTN_X,         XButton::X,              BUTTON_4,  "X"      },  // 3 is the unused "C" slot
  { BTN_Y,         XButton::Y,              BUTTON_5,  "Y"      },
  { BTN_START,     XButton::START,          BUTTON_12, "Start"  },
  { BTN_BACK,      XButton::BACK,           BUTTON_11, "Back"   },  // 9/10 are the digital L2/R2 slots
  { BTN_GUIDE,     XButton::XBOX,           BUTTON_13, "Guide"  },
  { BTN_LSHOULDER, XButton::LEFT_SHOULDER,  BUTTON_7,  "LB"     },  // 6 is unused
  { BTN_RSHOULDER, XButton::RIGHT_SHOULDER, BUTTON_8,  "RB"     },
  { BTN_LTHUMB,    XButton::LEFT_THUMB,     BUTTON_14, "LThumb" },
  { BTN_RTHUMB,    XButton::RIGHT_THUMB,    BUTTON_15, "RThumb" },
};

struct DpadName { uint16_t bit; const char* name; };
static const DpadName DPAD_NAMES[] = {
  { BTN_DPAD_UP, "DUp" }, { BTN_DPAD_DOWN, "DDown" }, { BTN_DPAD_LEFT, "DLeft" }, { BTN_DPAD_RIGHT, "DRight" },
};

struct ControllerState {
  uint16_t buttons;
  uint8_t leftTrigger;
  uint8_t rightTrigger;
  int16_t leftX, leftY;
  int16_t rightX, rightY;
};
// serviceBleReport() compares states with memcmp, which requires no padding.
static_assert(sizeof(ControllerState) == 12, "ControllerState must be padding-free");

static ControllerState g_lastState = {};
static unsigned long g_packetsOk = 0;
static unsigned long g_packetsBad = 0;
static uint32_t g_lastSummaryMs = 0;

// Latest rumble levels, set by onRumbleReceived() - the callback ESP32XInput invokes from
// inside pollRumble() (in loop()) whenever the console's rumble command CHANGES - and
// forwarded to Host by sendRumbleIfChanged().
static volatile uint8_t g_rumbleLeft = 0;
static volatile uint8_t g_rumbleRight = 0;
static volatile bool g_rumbleChangedSinceSent = false;

// ---- Shared helpers ----

// D-pad as a compass index: 0=Up 1=Up+Right 2=Right 3=Down+Right 4=Down 5=Down+Left
// 6=Left 7=Up+Left, 8=released (also returned for impossible opposing presses).
static uint8_t dpadDirection(uint16_t buttons) {
  const bool up    = (buttons & BTN_DPAD_UP) != 0;
  const bool down  = (buttons & BTN_DPAD_DOWN) != 0;
  const bool left  = (buttons & BTN_DPAD_LEFT) != 0;
  const bool right = (buttons & BTN_DPAD_RIGHT) != 0;
  if ((up && down) || (left && right)) return 8;
  static const uint8_t DIRECTION[3][3] = {  // [none/up/down][none/right/left]
    { 8, 2, 6 },
    { 0, 1, 7 },
    { 4, 3, 5 },
  };
  return DIRECTION[down ? 2 : (up ? 1 : 0)][left ? 2 : (right ? 1 : 0)];
}

static int16_t readLe16(const uint8_t* p) { return (int16_t)(p[0] | (p[1] << 8)); }

// ---- USB XInput output ----

// Translates g_lastState into a USB Xbox 360 HID report and sends it. Called once per
// drained batch of serial packets (see loop()): only the newest state reaches the console.
static void updateUsbState() {
  const uint16_t buttons = g_lastState.buttons;

  // ESP32XInput takes one combined hat value (like the real Xbox 360 report), and its
  // numbering is exactly dpadDirection()'s.
  ESP32XInput.setHat(dpadDirection(buttons));
  for (const ButtonMap& m : BUTTON_MAP) ESP32XInput.setButton(m.usb, (buttons & m.bit) != 0);

  // Triggers: wire format is 0..255, ESP32XInput wants 0..32768. Scaled here so the wire
  // protocol doesn't depend on any one Relay's output library.
  ESP32XInput.setLeftTrigger((uint16_t)(((uint32_t)g_lastState.leftTrigger * 32768UL) / 255UL));
  ESP32XInput.setRightTrigger((uint16_t)(((uint32_t)g_lastState.rightTrigger * 32768UL) / 255UL));

  // Sticks: signed int16 on the wire, XInput's native range - no rescaling.
  ESP32XInput.setStickLeft(g_lastState.leftX, g_lastState.leftY);
  ESP32XInput.setStickRight(g_lastState.rightX, g_lastState.rightY);

  ESP32XInput.send();
}

// ---- Wireless (BLE) output ----
// Read BLE_GAMEPAD_LIBRARY_NOTE, BLE_ALWAYS_RESIDENT_NOTE, BLE_XBOX_CONSOLE_NOTE and
// BLE_NO_PEER_NAME_NOTE at the top of this file before touching anything below.

// bleGamepad is allocated by ensureBleStarted(), not a global object, because its
// constructor needs a name built from ESP.getEfuseMac(), which isn't safe to call during
// static initialization. nullptr = BLE has never been started since boot.
static BleGamepad* g_bleGamepad = nullptr;

static ControllerState g_lastBleSentState = {};
static bool g_bleForceNextReport = true;   // send next report even if unchanged (e.g. right after connecting)
static uint32_t g_lastBleReportMs = 0;
static uint32_t g_lastBleWatchdogMs = 0;
static bool g_awaitingRepairDisconnect = false;
static uint32_t g_repairDisconnectIssuedMs = 0;
static PairState g_currentPairState = PairState::Idle;   // what we believe
static PairState g_lastSentPairState = PairState::Idle;  // what Host was last told

static inline bool bleConnected() { return g_bleGamepad != nullptr && g_bleGamepad->isConnected(); }

// >>> BLE_MAPPING_BEGIN
// ---- BLE_MAPPING_NOTE - the layout receivers actually assume for a generic HID gamepad ----
// A generic BLE HID gamepad has no spec-defined "A button" or "right stick": the receiver
// decides. Every mainstream stack (Linux evdev/SDL, Android, positional readers) converges
// on the layout the real Xbox/PlayStation descriptors follow:
//
//   AXES. The library's descriptor/report order is X, Y, Z, Rz, Rx, Ry (setHIDAxes()) -
//   Z/Rz come BEFORE Rx/Ry:
//       X  = left stick X        Z  = right stick X       Rx = left trigger
//       Y  = left stick Y        Rz = right stick Y       Ry = right trigger
//   (positional readers: axes 0,1 = LS; 2,3 = RS; 4,5 = triggers).
//
//   TRIGGERS are sent THREE ways at once because receivers disagree (a real DualShock
//   reports analog AND digital too):
//     - Brake (left) / Accelerator (right) simulation controls: what a real Xbox BLE pad
//       uses; Android/Linux treat them as the analog L2/R2.
//     - Rx (left) / Ry (right): DualShock-style and positional (SDL "a4/a5") readers.
//     - Buttons 9 (L2) / 10 (R2), digital, only at a (near) full pull - see below.
//
//   BUTTONS. HID "Button N" becomes gamepad button (N-1) of the Linux/Android table:
//   1=A 2=B 3=(C) 4=X 5=Y 6=(Z) 7=LB 8=RB 9=(L2) 10=(R2) 11=Back 12=Start 13=Guide
//   14=L3 15=R3. Slots 3, 6, 9, 10 are unused by face/shoulder buttons - that's why a
//   naive 1..11 table broke (X on the dead "C" slot, Y/LB/RB shifted, L3/R3 on L2/R2).
//
//   History: an earlier revision passed (LX, LY, LT, RX, RY, RT) to setAxes(), putting the
//   triggers on the receiver's RIGHT STICK slots and the real right stick on its TRIGGER
//   slots (a right stick stuck upper-left that moved when a trigger was pulled). The
//   per-axis setters used below are bound to the HID usage by name in every library
//   release, unlike setAxes(), whose parameter order changed between v0.7.1 and v0.7.2.

// The library has ONE axis range for every axis (set in ensureBleStarted()), so triggers
// travel through the same signed range as the sticks: released = -32767, full pull = +32767.
static inline int16_t bleClamp(int32_t v) { return (int16_t)(v < -32767 ? -32767 : (v > 32767 ? 32767 : v)); }
static inline int16_t bleAxis(int16_t v) { return bleClamp(v); }
// Host's wire format is XInput's (Y positive = UP); generic HID is the opposite (Y positive
// = DOWN), so both sticks' Y are inverted.
static inline int16_t bleAxisInverted(int16_t v) { return bleClamp(-(int32_t)v); }
static inline int16_t bleTrigger(uint8_t t) { return (int16_t)(((int32_t)t * 65534L) / 255L - 32767L); }
// Brake/Accelerator have their OWN range (0..32767, ensureBleStarted()), so released is 0.
static inline int16_t bleSimTrigger(uint8_t t) { return (int16_t)(((uint32_t)t * 32767UL) / 255UL); }

// Digital L2/R2 (buttons 9/10) is only a fallback for receivers that ignore the analog
// axes, so it must not fire early: Android prefers a pressed L2/R2 KEY over the analog
// axis, so a press at 12% pull made a good analog trigger look digital. It trips only at
// (almost) full pull, where analog reads ~1.0 anyway, with hysteresis so a trigger held at
// full doesn't chatter. Set BLE_TRIGGER_DIGITAL_PRESS to 256 to disable the digital buttons.
static const uint16_t BLE_TRIGGER_DIGITAL_PRESS = 250;
static const uint16_t BLE_TRIGGER_DIGITAL_RELEASE = 230;
static inline bool bleTriggerDigital(uint8_t value, bool wasDown) {
  return value >= (wasDown ? BLE_TRIGGER_DIGITAL_RELEASE : BLE_TRIGGER_DIGITAL_PRESS);
}

// BleGamepad hat constants indexed by dpadDirection()'s result.
static const uint8_t BLE_HAT[9] = {
  HAT_UP, HAT_UP_RIGHT, HAT_RIGHT, HAT_DOWN_RIGHT, HAT_DOWN, HAT_DOWN_LEFT, HAT_LEFT, HAT_UP_LEFT, HAT_CENTERED,
};

static inline void bleSetButton(uint8_t button, bool pressed) {
  if (pressed) g_bleGamepad->press(button);
  else g_bleGamepad->release(button);
}

// Translates g_lastState into ONE BLE HID report and sends it - the Wireless-mode
// counterpart of updateUsbState(). Auto-report is disabled (ensureBleStarted()), so the
// setters below only edit the pending report; nothing goes out until sendReport().
// Caller (serviceBleReport()) guarantees BLE is started and connected.
static void sendBleReport() {
  const uint16_t buttons = g_lastState.buttons;
  for (const ButtonMap& m : BUTTON_MAP) bleSetButton(m.ble, (buttons & m.bit) != 0);
  g_bleGamepad->setHat1(BLE_HAT[dpadDirection(buttons)]);

  g_bleGamepad->setX(bleAxis(g_lastState.leftX));            // left stick X
  g_bleGamepad->setY(bleAxisInverted(g_lastState.leftY));    // left stick Y
  g_bleGamepad->setZ(bleAxis(g_lastState.rightX));           // right stick X
  g_bleGamepad->setRZ(bleAxisInverted(g_lastState.rightY));  // right stick Y
  g_bleGamepad->setRX(bleTrigger(g_lastState.leftTrigger));  // left trigger
  g_bleGamepad->setRY(bleTrigger(g_lastState.rightTrigger)); // right trigger
  g_bleGamepad->setBrake(bleSimTrigger(g_lastState.leftTrigger));
  g_bleGamepad->setAccelerator(bleSimTrigger(g_lastState.rightTrigger));

  static bool l2Down = false, r2Down = false;  // remembered between reports for the hysteresis
  l2Down = bleTriggerDigital(g_lastState.leftTrigger, l2Down);
  r2Down = bleTriggerDigital(g_lastState.rightTrigger, r2Down);
  bleSetButton(BUTTON_9, l2Down);
  bleSetButton(BUTTON_10, r2Down);

  g_bleGamepad->sendReport();
}
// <<< BLE_MAPPING_END

// Called every loop() iteration while Wireless is active. Sends a report only when the
// state changed (or a fresh connection needs the full state once) and no more often than
// BLE_REPORT_MIN_INTERVAL_MS. Polled, not event-driven, so a change landing inside the
// rate-limit window is simply sent on a later iteration - the final state always gets out.
static void serviceBleReport() {
  if (!bleConnected()) {
    g_bleForceNextReport = true;  // whoever connects next needs the full current state
    return;
  }
  if (!g_bleForceNextReport && memcmp(&g_lastState, &g_lastBleSentState, sizeof(ControllerState)) == 0) return;

  const uint32_t now = millis();
  if (now - g_lastBleReportMs < BLE_REPORT_MIN_INTERVAL_MS) return;

  g_lastBleReportMs = now;
  g_bleForceNextReport = false;
  g_lastBleSentState = g_lastState;
  sendBleReport();
}

// ---- BLE advertising / connection control ----
// These reach past BleGamepad's public API into NimBLE - see BLE_ALWAYS_RESIDENT_NOTE.

static NimBLEAdvertising* bleAdvertising() {
  NimBLEServer* server = NimBLEDevice::getServer();
  return server ? server->getAdvertising() : nullptr;
}

static void startBleAdvertising() {
  NimBLEAdvertising* adv = bleAdvertising();
  if (adv != nullptr && !adv->isAdvertising()) adv->start();
}

static void stopBleAdvertising() {
  NimBLEAdvertising* adv = bleAdvertising();
  if (adv != nullptr && adv->isAdvertising()) adv->stop();
}

// Asks the connected peer (if any) to disconnect. Returns true if a disconnect was issued.
static bool disconnectBlePeer() {
  if (!bleConnected()) return false;
  NimBLEServer* server = NimBLEDevice::getServer();
  if (server == nullptr) return false;
  server->disconnect(g_bleGamepad->getPeerInfo(), BLE_ERR_REM_USER_CONN_TERM);
  return true;
}

// Brings up the BLE gamepad the first time it's needed (NOT at boot, so a Wired-only Relay
// never starts Bluetooth). begin() starts advertising by itself.
static void ensureBleStarted() {
  if (g_bleGamepad != nullptr) return;

  // Advertised name: "<RELAY_KIND> (<last 4 hex of MAC>)", e.g. "EverLink Relay (BEF0)" -
  // see EverLink_Protocol.md's "Kind as the basis for a wireless advertised name".
  char bleName[48];
  snprintf(bleName, sizeof(bleName), "%s (%04X)", RELAY_KIND, (unsigned)(ESP.getEfuseMac() & 0xFFFFu));
  g_bleGamepad = new BleGamepad(bleName, BLE_MANUFACTURER, BLE_INITIAL_BATTERY);

  // The descriptor is declared EXPLICITLY (not library defaults) so it is known and
  // stable: 15 buttons, 6 axes in the library's X,Y,Z,Rz,Rx,Ry order, Brake + Accelerator,
  // 1 hat. See BLE_MAPPING_NOTE for what each slot means to a receiver.
  BleGamepadConfiguration config;
  config.setAutoReport(false);              // one explicit sendReport() per update
  config.setAxesMin(-32767);                // the library has one range for ALL axes
  config.setAxesMax(32767);
  config.setModelNumber(BLE_MODEL_NUMBER);  // device name shown by SDL etc.
  config.setIncludeBrake(true);             // analog left trigger  (Simulation Controls page)
  config.setIncludeAccelerator(true);       // analog right trigger
  config.setSimulationMin(0);               // own range, separate from the axes: released = 0
  config.setSimulationMax(32767);
  config.setButtonCount(15);                // Button 1..15 (slots 3,6,9,10 are special - see BLE_MAPPING_NOTE)
  config.setHatSwitchCount(1);
  config.setWhichAxes(true, true, true, true, true, true, false, false);  // X,Y,Z,RX,RY,RZ on; sliders off
  g_bleGamepad->begin(&config);

  Serial.printf("BLE gamepad started as \"%s\" (15 buttons, 6 axes, 1 hat).\n", bleName);
}

// ---- Wireless mode lifecycle ----

// Entering (or re-entering) Wireless mode = start pairing (EverLink_Protocol.md section 5/6).
static void beginWirelessMode() {
  // The USB side stays enumerated (no supported way to shut ESP32XInput down), but don't
  // leave the console holding stuck inputs while we're not feeding it.
  ESP32XInput.releaseAll();
  ESP32XInput.send();

  const bool firstStart = (g_bleGamepad == nullptr);
  ensureBleStarted();  // on first start this also begins advertising
  g_bleForceNextReport = true;

  if (!firstStart) {
    // Re-pair request. If something is connected, drop it first (advertising can't offer a
    // new connection on top of an existing one) and wait for the link to really go down -
    // see sendPairStatusIfChanged().
    if (disconnectBlePeer()) {
      g_awaitingRepairDisconnect = true;
      g_repairDisconnectIssuedMs = millis();
    }
    startBleAdvertising();
  }
}

// Leaving Wireless mode: nothing may stay connected or discoverable. Idempotent and cheap,
// which lets the watchdog below call it repeatedly.
static void endWirelessMode() {
  g_awaitingRepairDisconnect = false;
  if (g_bleGamepad == nullptr) return;  // BLE never started - nothing on the air
  disconnectBlePeer();
  stopBleAdvertising();
}

// Runs from loop() while Wired is active. The library re-advertises by itself after a
// disconnect, which would silently undo endWirelessMode() - this keeps BLE silent.
static void enforceBleOffWhileWired() {
  if (g_bleGamepad == nullptr) return;
  const uint32_t now = millis();
  if (now - g_lastBleWatchdogMs < BLE_WATCHDOG_INTERVAL_MS) return;
  g_lastBleWatchdogMs = now;
  endWirelessMode();
}

// ---- Pairing status (Relay -> Host, EverLink_Protocol.md section 6) ----

// Sends the PAIR: line for a state unconditionally (bypassing "only if changed").
static void sendPairStateNow(PairState state) {
  static const char* const NAMES[] = { "idle", "searching", "connected", "disconnected" };  // PairState order
  g_lastSentPairState = state;
  Serial.printf("PAIR:%s\n", NAMES[(uint8_t)state]);
}

// Called every loop() iteration while Wireless is active. isConnected() is the source of
// truth for connect/disconnect; "searching" is tracked by this firmware (set when pairing
// is (re)triggered), not read from the library.
//   - link comes up                                      -> connected
//   - link goes down on its own (peer disconnected/unpaired us) -> disconnected
//   - link goes down because WE forced it for a re-pair  -> stays searching
static void sendPairStatusIfChanged() {
  const bool connected = bleConnected();

  if (g_awaitingRepairDisconnect) {
    if (!connected) {
      g_awaitingRepairDisconnect = false;  // the link we dropped on purpose is gone
      startBleAdvertising();               // don't rely on the library re-advertising in time
    } else if (millis() - g_repairDisconnectIssuedMs > REPAIR_DISCONNECT_TIMEOUT_MS) {
      g_awaitingRepairDisconnect = false;  // disconnect never happened; report what's true
    } else {
      return;  // still connected only because the drop hasn't landed yet - not news
    }
  }

  if (connected) g_currentPairState = PairState::Connected;
  else if (g_currentPairState == PairState::Connected) g_currentPairState = PairState::Disconnected;
  // Otherwise keep whatever setActiveMode() last set (Searching) or Disconnected.

  if (g_currentPairState != g_lastSentPairState) sendPairStateNow(g_currentPairState);
}

// Switches the active mode (MODE_WIRED or MODE_WIRELESS) - EverLink_Protocol.md section 5.
// Safe with the mode that's already active; for MODE_WIRELESS that is a deliberate re-pair.
//
// The MODE: acknowledgement is sent FIRST: Host discards PAIR: lines that arrive while it
// still believes the active mode is wired (that's how it filters stale status), so a
// PAIR:searching sent ahead of the ack would be thrown away on the very switch that
// triggers it.
static void setActiveMode(uint8_t newIndex) {
  g_activeModeIndex = newIndex;
  Serial.printf("MODE:%u\n", g_activeModeIndex);

  if (newIndex == MODE_WIRELESS) {
    beginWirelessMode();  // may block for a moment on first start (BLE stack init)
    g_currentPairState = PairState::Searching;
    sendPairStateNow(g_currentPairState);
  } else {
    endWirelessMode();
    // Nothing to report: Host ignores PAIR: while wired and resets its copy to idle on
    // the MODE: ack above. Just keep our bookkeeping consistent.
    g_currentPairState = PairState::Idle;
    g_lastSentPairState = PairState::Idle;
  }
}

// ---- Rumble reporting (Relay -> Host, v2+, EverLink_Protocol.md "Rumble") ----

// Registered with ESP32XInput.onRumble() in setup(). The library calls it from within
// pollRumble() only when the console's rumble command actually changes.
static void onRumbleReceived(uint8_t left, uint8_t right) {
  g_rumbleLeft = left;
  g_rumbleRight = right;
  g_rumbleChangedSinceSent = true;
}

// Forwards the latest rumble state as `RMBL:<left>:<right>`, once per change. Run after
// pollRumble(). %u (not Serial.print) is essential: Serial.print(uint8_t) writes the raw
// byte, not decimal digits - the same trap applies to every uint8_t printed in this file.
static void sendRumbleIfChanged() {
  if (!g_rumbleChangedSinceSent) return;
  const uint8_t left = g_rumbleLeft;
  const uint8_t right = g_rumbleRight;
  g_rumbleChangedSinceSent = false;
  Serial.printf("RMBL:%u:%u\n", left, right);
}

// ---- Serial input: identification, mode switch, data packets ----

// Identification reply: version, chip MAC (factory-burned, unique, stable across
// reflashing) and chip model (so Host can warn when the board lacks native USB), Kind, the
// Mode list, and the ACTIVE mode's index. The list is always sent in the same fixed order
// (indices stay stable for 0xFD), so the active mode can't be inferred from it and Host
// needs it stated - a rescan can happen while Wireless is active. %u keeps the version and
// index as decimal digits (Serial.print(uint8_t) would send a raw byte and Host's
// int.TryParse would never recognise the Relay).
static void sendIdentReply() {
  char macStr[13];  // 12 hex chars + null
  snprintf(macStr, sizeof(macStr), "%012llX", (unsigned long long)ESP.getEfuseMac());
  Serial.printf("%s%u:%s:%s:%s:%s:%u\n",
    IDENT_PREFIX, PROTOCOL_VERSION, macStr, ESP.getChipModel(), RELAY_KIND, RELAY_MODES, g_activeModeIndex);
}

// Handles the index byte following a consumed MODE_SWITCH_BYTE. The two bytes are sent
// back-to-back, so block briefly for the second rather than stall the parser; if it never
// comes, give up (the command byte stays consumed). An out-of-range index is silently
// ignored, matching the protocol doc's "does not reply if unsupported".
static void handleModeSwitch() {
  const uint32_t waitStart = millis();
  while (Serial.available() < 1) {
    if (millis() - waitStart > 50) return;
  }
  const uint8_t requestedIndex = (uint8_t)Serial.read();
  if (requestedIndex < MODE_COUNT) setActiveMode(requestedIndex);  // also sends the MODE: ack
}

// Reads and validates one data packet whose sync byte is at the head of the buffer and
// whose remaining bytes are already buffered. Returns true if g_lastState was updated; on
// a bad checksum the packet is dropped and the last good state held.
static bool readPacket() {
  uint8_t buf[PACKET_SIZE];
  if (Serial.readBytes(buf, PACKET_SIZE) != PACKET_SIZE) return false;  // can't happen after the availability check

  uint8_t checksum = 0;  // XOR of bytes[1..12] must equal buf[13]
  for (size_t i = 1; i < PACKET_SIZE - 1; i++) checksum ^= buf[i];
  if (checksum != buf[PACKET_SIZE - 1]) {
    g_packetsBad++;
    return false;
  }

  g_lastState.buttons      = (uint16_t)(buf[1] | (buf[2] << 8));
  g_lastState.leftTrigger  = buf[3];
  g_lastState.rightTrigger = buf[4];
  g_lastState.leftX  = readLe16(&buf[5]);
  g_lastState.leftY  = readLe16(&buf[7]);
  g_lastState.rightX = readLe16(&buf[9]);
  g_lastState.rightY = readLe16(&buf[11]);
  g_packetsOk++;
  return true;
}

// Drains everything currently buffered, dispatching on each lead byte: ping (0xFE), mode
// switch (0xFD) or data packet (0xA5). Anything else is a misaligned byte and is discarded
// to resync. A single dispatcher (rather than separate ping/mode/packet parsers) means a
// command that has other data buffered ahead of or behind it is still seen as a command
// rather than swallowed as garbage by the packet parser. Returns true if a valid packet
// updated g_lastState.
static bool processSerialInput() {
  bool gotPacket = false;
  while (Serial.available() > 0) {
    switch (Serial.peek()) {
      case PING_BYTE:
        Serial.read();
        sendIdentReply();
        break;
      case MODE_SWITCH_BYTE:
        Serial.read();
        handleModeSwitch();
        break;
      case SYNC_BYTE:
        if (Serial.available() < (int)PACKET_SIZE) return gotPacket;  // wait for the rest
        if (readPacket()) gotPacket = true;
        break;
      default:
        Serial.read();  // misaligned byte
        break;
    }
  }
  return gotPacket;
}

// ---- Debug output ----

static void appendName(char* buf, size_t size, const char* name) {
  const size_t len = strlen(buf);
  snprintf(buf + len, size - len, "%s%s", len ? "," : "", name);
}

static void printSummary() {
  const uint16_t buttons = g_lastState.buttons;
  char pressed[80] = "";  // all 15 names + separators come to ~68 chars
  for (const ButtonMap& m : BUTTON_MAP) if (buttons & m.bit) appendName(pressed, sizeof(pressed), m.name);
  for (const DpadName& d : DPAD_NAMES) if (buttons & d.bit) appendName(pressed, sizeof(pressed), d.name);
  if (pressed[0] == '\0') strcpy(pressed, "-");

  // ESP32XInput.ready() = "has been enumerated by whatever's on the USB port". Raw debug
  // telemetry only, NOT a trustworthy live indicator (it doesn't reliably clear on unplug -
  // see the top-of-file note). Host does not parse this line.
  Serial.printf(
    "[%lums] Mode:%s Btns:%-40s LT:%3u RT:%3u LX:%6d LY:%6d RX:%6d RY:%6d | ok:%lu bad:%lu USBEnumerated:%s BLEConnected:%s RumbleL:%3u RumbleR:%3u\n",
    millis(), (g_activeModeIndex == MODE_WIRELESS) ? "Wireless(BLE)" : "Wired(XInput)", pressed,
    g_lastState.leftTrigger, g_lastState.rightTrigger,
    g_lastState.leftX, g_lastState.leftY, g_lastState.rightX, g_lastState.rightY,
    g_packetsOk, g_packetsBad,
    ESP32XInput.ready() ? "yes" : "no",
    bleConnected() ? "yes" : "no",
    g_rumbleLeft, g_rumbleRight
  );
}

void setup() {
  Serial.begin(BAUD_RATE);
  delay(200);  // cosmetic: give a serial monitor a moment before we start printing
  Serial.println("=== EverLink Relay ===");
  Serial.printf("Waiting for packets at %lu baud...\n", BAUD_RATE);

  ESP32XInput.begin(XINPUT_VID, XINPUT_PID);
  ESP32XInput.setPollInterval(XINPUT_POLL_INTERVAL_MS);
  ESP32XInput.releaseAll();  // start fully released, not whatever memory held
  ESP32XInput.onRumble(onRumbleReceived);
  Serial.println("USB XInput controller initialized.");

  // BLE is deliberately NOT started here - see BLE_ALWAYS_RESIDENT_NOTE. setActiveMode()
  // just sets the starting mode and sends the initial MODE: line; Host doesn't strictly
  // need it (the next ident reply says which mode is active) but it saves a rescan if
  // Host is already watching this port.
  setActiveMode(MODE_WIRED);
}

void loop() {
  // Pings, mode switches and data packets, in whatever order they arrived.
  const bool gotPacket = processSerialInput();

  // Only the ACTIVE mode's machinery runs. A started BLE stack stays resident, but the
  // inactive transport is never fed input, polled, or allowed to advertise.
  if (g_activeModeIndex == MODE_WIRELESS) {
    serviceBleReport();         // rate-limited, change-only BLE HID report
    sendPairStatusIfChanged();  // connected / disconnected / searching -> Host
  } else {
    if (gotPacket) updateUsbState();
    ESP32XInput.pollRumble();   // services console rumble/LED packets; invokes onRumbleReceived()
    sendRumbleIfChanged();      // must run after pollRumble()
    enforceBleOffWhileWired();  // keep Bluetooth silent
  }

  const uint32_t now = millis();
  if (now - g_lastSummaryMs >= SUMMARY_INTERVAL_MS) {
    g_lastSummaryMs = now;
    printSummary();
  }
}
