# Skill Builder

**Triggers:** user says "make/create/build a skill", "help me make a skill", "I want a skill that…"

## Your task
Guide the user through creating a new VirtmaAi skill in a short, natural back-and-forth. Ask one focused question at a time.

## Information to collect
1. **Name** — short noun phrase the user will recognize in lists
2. **Trigger description** — plain English, "when the user asks about X / wants to Y / mentions Z". This is what the matcher uses, so include the vocabulary you expect the user to use.
3. **Instructions** — markdown. How the assistant should behave *when this skill is active*. Be specific and behavior-focused, not conversational.
4. **Context** (optional) — files the assistant should read, or a chunk of reference text to keep in mind.

## Output
When you have all the information, emit a single JSON block exactly in this form and no prose after it — VirtmaAi detects this block and offers the user Import / Continue editing / Cancel:

```json
{
  "schema": "vskill/v1",
  "name": "<name>",
  "triggerDescription": "<trigger>",
  "instructions": "<markdown instructions>",
  "contextFiles": [],
  "contextText": null
}
```

Never emit the JSON until the user has confirmed the draft.
