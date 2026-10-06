# BgColour

A system-tray app that computes one ambient colour from what's on screen and sends it to lights (Home Assistant, WLED). It's cheap enough to leave running while gaming.

## Tray

- The **icon is the live colour**. Pause bars mean paused or PC locked; three dots mean it's waiting for a fullscreen app.
- **Double-click** pauses or resumes.
- **Right-click menu:** pause; *Only while a fullscreen app is running*; outputs (toggle each, and *Restore light when stopped*); monitor; brightness; smoothing; vividness; update rate; *Edit config…*; *Start with Windows*; exit.
- Hover for the current colour and the status of each output. Output errors pop up as notifications.

Drag the icon from the hidden-icons area (^) onto the taskbar to keep the live colour visible.

## Convenience behaviour

- **Restores your light**: before taking over, it snapshots the Home Assistant light into `scene.bgcolour_restore_…` and restores it on pause, PC lock, fullscreen app closing (in fullscreen-only mode) or exit.
- **Pauses when the PC is locked** (`pauseWhenLocked`).
- **Fullscreen-only mode** drives the lights only while a game or video covers the whole captured monitor. It checks once a second.
- **Single instance**, plus command-line control of the running instance, for hotkey tools or a Stream Deck:
  ```
  BgColour.exe --toggle | --pause | --resume | --exit
  ```
- **Config auto-reload**: edits to the config file apply immediately.

## Config

`%APPDATA%\BgColour\config.json` is created on first run. The tray menu writes to the same file.

```json
{
  "monitor": 0,
  "fps": 30,                 // capture rate cap
  "smooth": 0.15,            // seconds; 0 = instant
  "sampleWidth": 64,         // GPU thumbnail width analysed
  "saturation": 3,           // how much vivid pixels outweigh grey ones (0 = plain average)
  "edge": 1.5,               // extra weight for screen edges
  "boost": 1.2,              // saturation boost of the result
  "brightness": 1,
  "crop": 0,                 // ignore outer fraction of each side (letterbox bars)
  "fullscreenOnly": false,
  "pauseWhenLocked": true,
  "homeAssistant": {
    "enabled": true,
    "url": "https://hass.insi.dev",
    "entity": "light.shelf_light_light",
    "intervalMs": 1000,      // Zigbee bulbs: keep >= 500
    "restoreOnStop": true
    // "token": "..."        // optional; otherwise the HA_TOKEN environment variable is used
  },
  "wled": { "enabled": false, "host": "", "port": 21324, "leds": 0 }   // leds 0 = ask the device
}
```

Other options: `--config PATH`, `--console` (live colour in a console), `--debug` (raw capture stats), `--list` (monitors).

## How it stays cheap

1. **DXGI Desktop Duplication**: the frame never leaves the GPU, and `AcquireNextFrame` only returns when the screen changes, so a static screen costs almost nothing.
2. **GPU downscale**: the frame is copied into a mipmapped texture and `GenerateMips` box-filters it. Only a ~64×36 mip is read back.
3. **Tiny CPU pass**: about 2,000 pixels per frame, capped at `fps`, at *BelowNormal* priority. While paused, the GPU resources are released completely.

Measured on a 4K HDR desktop with an RX 480: ~0.3 % of one CPU core when idle and ~3 % of one core with the screen changing constantly. RAM is ~70 MB, mostly WinForms.

## Colour weighting

- **Hue** is a weighted average where weight ∝ brightness² × (1 + `saturation` × pixel saturation) × edge bonus. Vivid colours win over a grey UI or HUD, and screen edges (nearest the LEDs) count more.
- **Brightness** is the plain average scene brightness, so dark scenes give dim LEDs.
- Then a saturation `boost` and exponential smoothing.

## Notes

- **HDR**: with Windows HDR on, it captures the real FP16 scRGB frame (`DuplicateOutput1`), averages it in linear light on the GPU, and maps the Windows "SDR content brightness" level to full white. HDR highlights clip to full. The legacy 8-bit capture would blow colours out (orange turns yellow).
- Works with borderless, windowed and most fullscreen games. If a game takes true exclusive fullscreen, capture can drop briefly; it's recreated automatically.
- *Start with Windows* registers the current exe path. Move the build somewhere permanent first, e.g. with `dotnet publish -c Release -o %LOCALAPPDATA%\Programs\BgColour`.
