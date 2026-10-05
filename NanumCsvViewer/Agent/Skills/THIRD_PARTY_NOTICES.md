# Third-party notices: analysis skill pack

The skill folders in this directory (every folder except `nanum-python-analysis`) are copied **unmodified** from

- **K-Dense-AI/scientific-agent-skills** — https://github.com/K-Dense-AI/scientific-agent-skills
- tag **v2.72.0**, commit `526ebce143bc326f44471554e33d66a19c840360`
- Copyright (c) 2025 K-Dense Inc. — **MIT License** (full text in `LICENSE-K-Dense-scientific-agent-skills.txt`)

The MIT notice must stay with every copy of these files; the app extracts it next to the skills.

The `license` field in an individual `SKILL.md` names the license of the library that skill describes
(for example BSD-3-Clause for scikit-learn or seaborn); it does not change the license of the skill text.

`nanum-python-analysis` is written for Nanum CSV Viewer and is part of the app.

## What the pack is

Skills are third-party guidance text and small local helper scripts for an AI agent. The app does not validate them.
Results produced with them are labelled "Python 분석 (앱 검증 범위 밖) / Python analysis (outside the app's validated tools)".
They are for research and aggregate analysis, not patient-specific diagnosis or treatment.

## Selection

`manifest.json` lists every included skill with its category, Python dependencies and the reason it was chosen, and the
skills that were reviewed and rejected (network access, GPU, uploads, patient-specific scope) with the reason.
The pinned scripts were scanned for network calls, subprocess use and credentials; none of the included scripts call
external services. A few `SKILL.md` files mention optional network steps (installing packages, downloading sample data);
`nanum-python-analysis` forbids them.

## Updating

Replace the skill folders from a newer upstream tag, update the tag and commit in `manifest.json` and in this file,
re-run the manifest checks (`SkillPackTests`), and review the scripts again.
