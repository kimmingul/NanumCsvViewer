---
name: nanum-python-analysis
description: Read FIRST before any Python analysis of data exported with csv.export_view. Nanum CSV Viewer rules for the data policy on printed output, reproducibility header, honest reporting, report/figure files, the "outside the app's validated tools" label and the clinical research-only boundary. Covers data\*.csv + schema.json of the analysis folder.
license: MIT
metadata:
  version: "1.0"
  skill-author: Nanum CSV Viewer
---

# Nanum Python analysis (앱 전용 규칙)

These rules sit **above** every other analysis skill in this pack (K-Dense scientific skills are third-party guidance).
If a skill says something that conflicts with this page, this page wins. 이 문서가 다른 스킬과 충돌하면 이 문서가 우선합니다.

## 0. Use the app first (앱 도구 먼저)

Python is only for analyses the app does **not** provide, or when the user explicitly asks for Python.
Check `csv.run_analysis` first; it already has descriptive statistics, frequencies, t-tests, ANOVA, chi-square,
correlation, normality, GLM/ANCOVA/RM-ANOVA/GLzM/logistic/LMM/NLMM, non-parametric tests, Kaplan-Meier/Cox,
k-means/KNN/naive Bayes/trees/random forest/SVM/gradient boosting/AdaBoost/AutoML, PCA/LDA and quality checks.
Always say in the answer which path you used: "앱 검증 도구 / App-validated tool" or "Python".

Typical Python-only work: PK/PD and NCA parameter tables, Bland-Altman and other agreement analyses, power and sample
size, mixed designs the app lacks, competing risks, time series models, Bayesian models, SHAP explanations,
publication figures, large-data processing (polars, dask), method validation tables.

## 1. Get the data (데이터)

1. Call `csv.export_view` while the right tab is active. It writes into the **analysis folder** (your working
   directory, usually `<name>_분석결과`):
   - `data/<name>.csv` : UTF-8 without BOM, header row, comma, an empty cell is missing. The file is the **current
     view**: filters, sort, cell edits, inserted/deleted rows and columns are already applied.
   - `data/<name>.schema.json` : `source_file`, `sheet`, `exported_at`, `row_count`, `source_total_rows`, `filters`
     (kind/column/text), `filter_combination`, `sort`, `columns[]` (`name`, `type`, `numeric`, `dtype_hint`).
2. Read it with `pd.read_csv(path, encoding="utf-8")`. Convert dates and currency/percent columns using
   `dtype_hint`. Codes and identifiers stay text (`dtype=str`) so leading zeros survive.
3. The export is a snapshot. When the user changes the filter or edits, export again and say so.
4. Never read the original source file around the app, never write outside the analysis folder, never modify the
   source. Do not use the network: no downloads, no `sns.load_dataset`, no cloud paths, no model hubs, no telemetry.
5. Never install packages yourself (no `pip install`, `uv pip`, `conda`), whatever another skill says. Use what the
   Python section of the guide says (the managed environment and the `py.ensure_packages` tool when it exists);
   otherwise tell the user which package is missing.

## 2. Data policy for what you print (출력 정책)

Everything a script prints is read by the AI model; the app cannot enforce this for code you write.

- **Summary only** (default): print aggregates and model output only: counts, means, SD, quantiles, coefficients,
  confidence intervals, p-values, tables of group summaries, plot descriptions. **Never** print raw rows or
  identifiers: no `print(df)`, `df.head()`, `df.sample()`, `df.to_string()`, no value lists of ID/name/date columns,
  no exception text that echoes cell values (catch and print only the exception type and a generic message).
  Small cells: do not print a group summary whose group has fewer than 5 rows; write "n<5" instead.
  Save per-row results (residuals, predictions, per-subject parameters) to a file in the analysis folder; do not print them.
- **Rows with approval**: ask the user before printing any row-level value, name the columns and the number of rows
  you need, and print only that. If the user declines, work from aggregates.
- **Rows allowed**: still print only what the question needs.
- Figures and reports go to files; the user opens them in the app viewers. A figure can contain raw points: under
  Summary only, avoid per-subject labels and annotate with group-level information.

## 3. Script conventions (스크립트)

- Write every analysis as a `.py` file in the analysis folder (`write` tool), run it, and keep it for the user.
  Name it simply (letters, digits, `_`, `-`). Start from `skill://nanum-python-analysis/assets/analysis_template.py`.
- **Reproducibility header** at the top of every script (docstring) and at the top of the report:
  source file (`schema["source_file"]`, sheet), exported file, **row count used and dropped**, active filters
  (`schema["filters"]`), package versions actually imported (`importlib.metadata.version`), timestamp, random seed,
  and the **skill(s) you followed** (e.g. `Skills: nanum-python-analysis, pkpd-modeling`). The template has
  `repro_header()` that builds it.
- Set seeds (`random_state`, `np.random.default_rng(seed)`) and report them. Do not change the data silently:
  every dropped or imputed row is counted and reported.
- Korean text in figures: `plt.rcParams["font.family"] = "Malgun Gothic"; plt.rcParams["axes.unicode_minus"] = False`.
- Keep printed output short (about 60 lines). Fix editor diagnostics before trusting a result.

## 4. Outputs the app can open (결과 파일)

- Markdown report: `<name>_report.md` in the analysis folder; then `csv.show_markdown` opens it in the app viewer.
- Figures: `figures/<name>.png` (dpi 200, `bbox_inches="tight"`; SVG/PDF also fine); then `csv.show_image`.
- Tables for reuse: `tables/<name>.csv` (UTF-8). Relative links only inside reports: `![](figures/hist.png)`.
- Summarise the finding in the chat in a few sentences and point to the report.

## 5. Honest reporting (정직한 보고)

Every report states: the question; the method and **why this method** (and what the app's validated tool would have been,
if any); n analysed, n excluded and why; assumptions checked and the result of each check (normality, variance,
independence, proportional hazards, linearity, positivity of concentrations ...); model convergence and warnings
(do not hide `ConvergenceWarning`, singular matrices, boundary fits, `LinAlgError`); effect sizes with confidence
intervals, not only p-values; **multiple testing** (how many tests were run; Holm/Bonferroni/BH adjustment named);
sample-size or power limits; what the analysis cannot say (association is not causation; small samples; selection).
Do not report a number you did not look at. Do not tune the analysis until it is significant; report every
analysis you ran. If something failed or was approximated, say that plainly.

## 6. Label (표기)

The first lines of every report and of every chat answer that presents Python results carry this label:

> **Python 분석 (앱 검증 범위 밖) / Python analysis (outside the app's validated tools)** —
> generated by a script in this folder; not covered by the app's validated statistics. Review before use.

When the app's tool and Python both answered the same question, show both and say if they differ.

## 7. Clinical boundary (임상 경계)

This pack supports **research and aggregate analysis only**: cohort/trial summaries, PK/PD research, method
evaluation. It is **not** for patient-specific diagnosis, treatment, dosing or triage, and the output is not a medical
device result. If the user asks about an individual patient's care (individual dose, diagnosis, prognosis of a named
person), decline that part, say it needs a qualified clinician, and offer the aggregate/research version. Use
synthetic or de-identified data for examples. Never put direct identifiers (names, national IDs, phone numbers,
exact dates of birth) in a report or figure. Skill outputs for clinical topics still need review by a qualified person.
