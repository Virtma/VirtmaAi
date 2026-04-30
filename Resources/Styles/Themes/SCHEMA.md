# VirtmaAi Theme Schema (`vtheme/v1`)

A theme is a JSON document describing colors, typography, spacing, and radii. VirtmaAi detects theme blocks in assistant messages and prompts the user to apply them.

## Top-level
```json
{
  "schema": "vtheme/v1",
  "name": "My Theme",
  "author": "optional",
  "description": "optional",
  "baseMode": "Dark" | "Light",
  "palette": { ... },
  "typography": { ... },
  "spacing": { ... },
  "radii": { ... }
}
```

## Palette (all hex strings, `#RRGGBB` or `#AARRGGBB`)
| Key | Purpose |
|---|---|
| `primary` | Main brand accent — used on primary buttons, links, active states |
| `primaryPressed` | Darker pressed state of primary |
| `secondary` | Interactive accent (hover/focus) |
| `surfaceBase` | App background |
| `surfaceAlt` | Elevated panels (sidebar, headers) |
| `surfaceElevated` | Modal / card background |
| `onSurface` | Primary text color |
| `onSurfaceMuted` | Secondary text, labels |
| `onSurfaceFaint` | Tertiary text, placeholders |
| `border` | Divider / stroke color |
| `error` `warning` `success` `info` | Status colors |

## Typography
- `fontFamily`, `fontFamilyBold` — must match registered MAUI font keys (default: `OpenSansRegular`, `OpenSansSemibold`)
- `baseSize`, `smallSize`, `largeSize`, `headingSize` — point sizes

## Spacing / Radii
`xs sm md lg xl` → point sizes.

## Detection
Assistant messages containing a fenced ```json block with `"schema": "vtheme/v1"` trigger the Apply / Save / Ignore prompt in VirtmaAi.
