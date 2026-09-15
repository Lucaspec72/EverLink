# EverLink
<img width="512" height="512" alt="Icon" src="https://github.com/user-attachments/assets/345de6ed-42ea-4864-8a8a-8717a6254282" />

> ⚠️ **VIBECODED SOFTWARE**
>
> EverLink is a vibecoded project, built with the help of **Claude (Free)**.
> Expect bugs and questionable code, use at your own risk.

EverLink lets you use a controller connected to a PC to send its inputs to a console through an **ESP32 USB relay**.

The PC reads the controller inputs, sends them to the ESP32, and the ESP32 presents itself to the console as an Xbox 360/XInput controller.

```text
Controller
    │
    ▼
   PC
 EverLink
    │
    │ USB
    ▼
  ESP32
  Relay
    │
    │ USB HID
    ▼
 Console
```

## Features

* 🎮 Supports Xbox, PlayStation, Switch Pro and other controllers recognized by SDL3
* 🔄 Real-time controller input streaming
* 🔌 ESP32 relay outputs a standard Xbox 360/XInput controller over USB
* 🕹️ Button, stick and trigger remapping

## Requirements

### PC

* Windows 10/11
* A controller supported by SDL3

### ESP32 Relay

The relay requires an ESP32 with **native USB / USB OTG** support.

Supported families:

* ESP32-S2
* ESP32-S3
* ESP32-P4

Regular ESP32 and ESP32-C3 boards do **not** have the required native USB functionality for the relay's controller output.

The relay firmware uses the **ESP32XInput** library.


## Usage

Once the EverLink host is launched, if the program doesn't auto-detect the relays, press "Rescan" to look for compatible EverLink relays. once found, you may rename the relays by right clicking on them and selecting "Rename".
To map a controller to a relay, go into it's configuration menu (configure) and select your controller of choice from the dropdown selector named "controller" (to assist in finding the proper controller in a list, a green indicator will light up whenever a controller is active.)
From there, you can either choose to apply remapping settings (which can be set and saved similarly to the Dolphin emulator), or directly start playing ! (note: The host purposefully stops sending controller inputs to the relay when the configuration window is open)

## Building

### Host

From the `Host` directory:

```powershell
dotnet build
```

Run with:

```powershell
dotnet run
```

For a self-contained executable:

```powershell
dotnet publish -c Release
```

### Relay

Open `Relay/EverLinkRelay.ino` in Arduino IDE and install:

* ESP32 board support by Espressif
* ESP32XInput

Select your ESP32-S2/S3/P4 board and enable its **USB OTG / TinyUSB** mode before uploading.

See the relay documentation for board-specific flashing instructions.

## How It Works

EverLink is split into two parts:

**Host**

Reads controller inputs through SDL3, applies any configured remapping, and sends the resulting controller state to the relay.

**Relay**

Receives the controller data over serial and converts it into USB HID reports so the console sees the ESP32 as an Xbox 360/XInput controller.

The communication between the two is defined by the EverLink serial protocol.

## Project Structure

```text
EverLink/
├── Host/                  # Windows application
├── Relay/                 # ESP32 firmware
└── EverLink_Protocol.md   # Host ↔ Relay protocol
```

## Disclaimer

This project is experimental and actively developed. It was built primarily through AI-assisted "vibecoding", so the codebase may contain bugs, rough edges, or questionable engineering decisions.

If something breaks, open a bug report and I'll try and look at it when I can.
