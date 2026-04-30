# VirtmaAi Skill Schema (`vskill/v1`)

```json
{
  "schema": "vskill/v1",
  "name": "Short human-readable name",
  "triggerDescription": "Plain-English description of when this skill applies",
  "instructions": "Markdown instructions injected into the system prompt when the skill matches",
  "contextFiles": ["optional/absolute/or/relative/paths"],
  "contextText": "optional free-form context to keep in mind"
}
```

VirtmaAi detects a fenced ```json block containing `"schema": "vskill/v1"` in assistant output and offers the user an Import / Continue editing / Cancel prompt.
