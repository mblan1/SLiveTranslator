# IconGen

Renders the SLiveTranslator app icon with WPF vector drawing. Each size
(16, 20, 24, 32, 40, 48, 64, 256 px) has its own hand-tuned layout in
`LayoutFor()`: small sizes snap to whole pixels and use square-capped bars;
48 px and up add rounded caps, a soft top sheen and a bubble shadow.

## Regenerate

```
dotnet run --project tools/IconGen -- <repoRoot>
```

`<repoRoot>` is optional; without it the tool walks up to `SLiveTranslator.slnx`.
It writes:

| File | Content |
| --- | --- |
| `src/SLiveTranslator/Assets/app.ico` | 8 images: 16–64 px as 32-bit BGRA DIBs, 256 px as PNG |
| `src/SLiveTranslator/Assets/app-256.png` | 256 px image for README / About |
| `tools/IconGen/preview.png` | all sizes 1:1 plus 16/24/32 px at 8x, on light and dark |

It then re-reads `app.ico`, checks the directory and decodes it with WPF; a
non-zero exit code means the check failed. Look at `preview.png` after any
design change. The tool is not part of `SLiveTranslator.slnx`.

## Tray icon at runtime

Do not ship a separate tray file. Load `app.ico` at the system small-icon size
(`SystemInformation.SmallIconSize`, i.e. `GetSystemMetrics(SM_CXSMICON)`: 16 px
at 100 %, 20 px at 125 %, 24 px at 150 %) so Windows picks the matching
hand-tuned frame instead of scaling another one, e.g.
`new Icon(stream, SystemInformation.SmallIconSize)`.

While translation is live, draw a green "live" dot (`TrayIcon.Live`, #43A047;
about 40 % of the icon size, bottom-right corner, with a 1 px dark outline so
it stays visible on light and dark taskbars) onto a copy of that bitmap and
swap it into `NotifyIcon.Icon`. Drop the dot when paused. This replaces the
drawn "T" circle in `src/SLiveTranslator/UI/TrayIcon.cs`.
