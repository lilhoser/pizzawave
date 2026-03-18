# pizzaui - Windows UI Application

`pizzaui` is the Windows Forms UI for PizzaWave.

> Deprecated: `pizzaui` is maintenance-only. New UI features and active improvements are focused on `pizzapi`.

## Summary

- Receives and transcribes live callstream traffic
- Supports offline capture loading
- Manages alerts and email notifications
- Includes grouping, search, export, and audio playback controls

For Linux/macOS, use [pizzapi](../docs/pizzapi.md).

## Requirements

- Windows
- .NET 9 runtime (or self-contained build)
- trunk-recorder + callstream plugin

## Configuration

Shared settings file:

- `%APPDATA%\pizzawave\settings.json`

Managed by `pizzaui`:

- call display grouping/sorting options
- alert behavior
- email credentials for alerts (`emailUser`, `emailPassword`)

Core settings reference: [pizzalib README](../pizzalib/README.md)

## Usage Highlights

- Open capture: `File -> Open capture`
- Open offline capture: `File -> Open offline capture`
- Manage alerts: `Edit -> Alerts`
- Export calls: `View -> Export`
- Search: `Ctrl+F`

## Headless Mode

```powershell
pizzaui.exe --headless --settings=<path_to_settings.json>
```

Useful for service-style runs on Windows.

## See Also

- [Main docs](../docs/README.md)
- [pizzapi docs](../docs/pizzapi.md)
- [pizzacmd docs](../pizzacmd/README.md)
- [pizzalib docs](../pizzalib/README.md)
