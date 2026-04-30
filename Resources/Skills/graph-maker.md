# Graph-maker skill

When the user asks for a chart, graph, or visualization of data, emit a fenced JSON block with the language tag `vchart`. VirtmaAi will detect the block and render it inline using a native SkiaSharp chart view — do not inline images or ASCII art.

## Chart schema

```json
{
  "type": "bar | line | scatter",
  "title": "optional string",
  "axes": { "x": "optional x label", "y": "optional y label" },
  "data": [
    {
      "label": "Series name",
      "categories": ["Jan", "Feb", "Mar"],
      "values": [10, 20, 15],
      "color": "#E10600"
    }
  ]
}
```

Rules:
- `type` is required and must be `bar`, `line`, or `scatter`.
- `data` is an array of one or more series. Each series must include `values`.
- `categories` is optional on all series; when present on the first series it is used as the x-axis labels for bar charts.
- `color` is optional hex (e.g. `#FF3B30`). When omitted VirtmaAi picks from the default palette.
- For multi-series charts, all series should share the same number of values so they align to categories.
- Prefer small-cardinality data (≤ 40 points per series). For large datasets, summarize first.

## Usage patterns

### Bar chart

````
```vchart
{
  "type": "bar",
  "title": "Monthly active users",
  "axes": { "x": "Month", "y": "Users" },
  "data": [
    { "label": "2025", "categories": ["Jan","Feb","Mar","Apr"], "values": [120, 145, 170, 210] }
  ]
}
```
````

### Line chart with multiple series

````
```vchart
{
  "type": "line",
  "title": "CPU vs memory",
  "data": [
    { "label": "CPU %", "values": [12, 18, 22, 35, 28, 20], "color": "#E10600" },
    { "label": "Mem %", "values": [40, 42, 45, 50, 48, 44], "color": "#35B4F0" }
  ]
}
```
````

### Scatter

````
```vchart
{
  "type": "scatter",
  "title": "Latency samples",
  "data": [
    { "label": "p50", "values": [120, 130, 140, 125, 135, 128] }
  ]
}
```
````

If the user asks for something not directly chartable (e.g. a 3D surface, a Sankey, a network graph), explain that and suggest the closest supported shape, or fall back to a textual summary.
