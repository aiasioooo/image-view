# Image Viewer Autoscale

Minimal WPF image viewer focused on zooming and comparing scaled variants.

## Run

```powershell
dotnet run -- "C:\path\to\image.png"
```

Multiple image paths open in separate windows. If no path is provided, the app opens a file picker.

## Requirements

- Windows 10/11
- .NET 8 SDK to build/run from source
- .NET 8 Desktop Runtime to run a framework-dependent publish without the SDK
- Vulkan-capable GPU and current GPU driver for ML upscale modes

Non-ML modes do not require external models or tools. ML modes use an external Real-ESRGAN NCNN Vulkan compatible executable configured at:

```text
%LocalAppData%\ImageViewerAutoscale\upscalers.json
```

Expected Real-ESRGAN models:

- `realesr-animevideov3` for key `2`, fast anime 2x
- `realesrgan-x4plus-anime` for key `3`, quality anime 4x

The Real-ESRGAN NCNN Vulkan release normally includes its model files in a `models` folder next to `realesrgan-ncnn-vulkan.exe`. This app does not include those external binaries or model files in source control.

## Windows Context Menu

Install the per-user right-click menu for image files:

```powershell
.\scripts\install-context-menu.ps1
```

This publishes the app to `bin\Release\net8.0-windows\win-x64\publish` if needed, then registers `Open with Image Viewer Autoscale` under `HKCU`. No admin rights are required.

Register it as the default image handler for common image extensions:

```powershell
.\scripts\install-context-menu.ps1 -MakeDefault
```

Windows 10/11 protects existing default-app choices with a `UserChoice` hash. The script registers the app correctly and sets the per-user extension defaults where Windows allows it, but you may still need to pick **Image Viewer Autoscale** once in Windows Settings or the **Open with** dialog for extensions that already have a protected default.
When `-MakeDefault` is used, the script also removes protected per-user `UserChoice` overrides for the supported image extensions so Windows can fall back to the app ProgID.

Remove it:

```powershell
.\scripts\uninstall-context-menu.ps1
```

## Controls

- Mouse wheel: stepped zoom around cursor
- Ctrl + mouse wheel: smoother zoom around cursor
- Left drag: pan
- Space: pause/unpause animated GIF
- Up / Down while paused: previous / next GIF frame
- Left / Right / A / D: previous / next image in the same folder
- F: open the image folder in Explorer
- L: lock zoom to the image's larger dimension and clamp dragging at image borders
- Ctrl + F: fit image to window
- B: toggle black/white background
- K: toggle keep-aspect resize mode
- R: toggle sampled 1px window border
- H: help
- Ctrl + 1: 100% zoom
- 0: original pixels with nearest-neighbor rendering
- 1: original image with WPF high-quality display scaling
- 2: fast anime ML 2x render
- 3: quality anime ML 4x render
- 8: cached high-quality 2x render
- 9: cached high-quality 4x render
- Ctrl + O: open more images
- Drag image files onto the window: open each in a new window

Animated GIF playback is supported for original/pixel-inspect display. Generated scaling modes are skipped for animated GIFs.

Move-window mode:

- T: toggle move-window mode when not maximized
- P while in T mode: toggle always-on-top
- W while in T mode: toggle pin-to-window mode

In T mode, dragging moves the window instead of panning the image. Zoom and normal viewer keys are disabled except T/P/W. Leaving T mode clears the active topmost/pinned owner state, but remembers whether P or W was selected for the next T-mode entry in that same window.

## Scaling Pipeline

The viewer always reacts immediately to zoom and pan using the best bitmap already available. After zooming settles, it queues likely future variants based on recent zoom level and selected mode.

Cached outputs are stored under:

```text
%LocalAppData%\ImageViewerAutoscale\cache
```

Cached generated PNGs are automatically removed after 3 hours without use. Cleanup runs opportunistically and is throttled.

External ML upscalers are configured at:

```text
%LocalAppData%\ImageViewerAutoscale\upscalers.json
```

The app creates this file on first run. Point `Executable` to a tool such as `real-esrgan-ncnn-vulkan.exe` or `waifu2x-ncnn-vulkan.exe`, and adjust `Arguments`. The supported placeholders are:

- `{input}`
- `{output}`
- `{scale}`

Example for Real-ESRGAN NCNN Vulkan:

```json
{
  "AnimeMl2x": {
    "Executable": "C:\\tools\\realesrgan-ncnn-vulkan\\realesrgan-ncnn-vulkan.exe",
    "Arguments": "-i \"{input}\" -o \"{output}\" -n realesr-animevideov3 -s 2 -f png"
  },
  "AnimeMl4x": {
    "Executable": "C:\\tools\\realesrgan-ncnn-vulkan\\realesrgan-ncnn-vulkan.exe",
    "Arguments": "-i \"{input}\" -o \"{output}\" -n realesrgan-x4plus-anime -s 4 -f png"
  }
}
```
