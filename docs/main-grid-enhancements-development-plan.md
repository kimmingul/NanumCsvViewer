# Main Grid Enhancements Development Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve the main CSV grid with Excel-like cell selection/copy, row and column copy commands, typed header filters for categorical/date columns, inspector copy actions, and date range filtering.

**Architecture:** Keep the existing `MainWindowController` behavior as the integration point, but move new state and formatting logic into small focused types. Selection becomes a grid-cell selection model independent of `NSTableView` row selection, copy/export formatting becomes reusable formatter code, and column filters become structured filter state that compiles into the existing `VirtualCsvDocument.applyFilter` pipeline.

**Tech Stack:** Swift 6.3, AppKit `NSTableView`, `NSMenu`, `NSPopover`, `NSPasteboard`, `NSDatePicker`, existing `VirtualCsvDocument`, existing `ColumnStatisticsReport`, and XCTest.

## Global Constraints

- No new third-party dependencies.
- Keep large CSV handling responsive: long-running row scans must run through existing background operation/cancellation patterns.
- Preserve existing text filter, "Filter by Cell", sort, hidden-column, saved-view, and export behavior.
- Multi-cell copy should paste naturally into Excel/Numbers/Sheets as TSV.
- Header value filters apply only to columns inferred as `Categorical` or `Date`.
- Date range filters must compare parsed dates, not raw strings.
- New UI text must be localized through `L.t`.
- Tests must follow RED/GREEN: add failing tests before implementation changes.

---

## Current Code Context

The main grid is implemented primarily in:

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/CsvTableView.swift`
- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/SortHeaderCell.swift`
- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/CsvTableHeaderView.swift`
- `NanumCsvViewerMac/Sources/CsvCore/VirtualCsvDocument.swift`
- `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`
- `NanumCsvViewerMac/Tests/CsvCoreTests/VirtualCsvDocumentTests.swift`

Important existing behavior:

- `MainWindowController.configureTable()` sets `tableView.allowsMultipleSelection = false`.
- `CsvTableView` reports clicked row/column through `cellClickHandler`.
- The app currently tracks one selected data cell via `tableView.selectedRow` plus `currentDataColumn`.
- Cell rendering highlights only the selected row/current column cell.
- Context menu already includes copy cell, copy as CSV, copy as JSON, filter by cell, hide column, and sort commands.
- Header clicks currently sort through `tableView(_:didClick:)`.
- Existing filters are held as `textCondition` plus `valueConditions`, then compiled through `combinedPredicate()` and applied with `VirtualCsvDocument.applyFilter`.
- `ColumnStatisticsReport` already exposes inferred types and top values, which can drive filter availability and default display.
- Inspector content is rendered into `detailTextView` by `updateDetailPanel()` and column-statistics actions.

## Desired User Experience

### Main Grid Selection

Users should be able to:

- Click a cell to select it.
- Drag across cells to select a rectangular range.
- `Shift + click` to extend from an anchor cell to a rectangular range.
- `Command + click` to toggle individual cells or ranges.
- Press `Cmd+C` to copy the selected cell/range as TSV.

Note: On macOS, `Control + click` is commonly interpreted as secondary click. Use `Command + click` for additive selection instead of `Control + click`.

### Context Menu Copy Commands

When a user right-clicks a data cell:

- Existing single-cell copy commands remain.
- Add `Copy Entire Row`.
- Add `Copy Entire Column`.
- Commands operate against the current filtered/sorted visible view.

### Header Filters

For columns inferred as `Categorical` or `Date`:

- Header shows a filter affordance without breaking sort.
- Clicking the header title still sorts.
- Clicking the filter affordance opens a popover.
- Categorical popover supports search, checkbox selection, select all, clear, apply, and cancel.
- Date popover supports date range filtering and optionally value checkbox filtering for date values.
- Active filters show filter tokens and header visual state.

### Inspector Copy

When the inspector shows a selected row:

- Add `Copy (TEXT)`.
- Add `Copy (JSON)`.
- TEXT copies the current readable inspector content.
- JSON copies the selected row as a JSON object.

When the inspector shows non-row content such as column statistics or performance:

- `Copy (TEXT)` remains enabled.
- `Copy (JSON)` is disabled unless a structured JSON representation is available.

## Recommended Implementation Order

1. Add grid-cell selection model and multi-cell rendering.
2. Add TSV copy for selected cells.
3. Add context-menu row/column copy.
4. Add inspector TEXT/JSON copy.
5. Introduce structured column filter state.
6. Add distinct value collection in `VirtualCsvDocument`.
7. Add categorical/date header filter popovers.
8. Add date range filtering.
9. Persist new filters in saved views.

This order keeps every milestone independently useful and testable.

---

## File Structure

### New Files

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/GridSelectionModel.swift`
  - Owns selected grid cells, anchor cell, range selection, additive selection, clearing, and containment checks.

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/GridCopyFormatter.swift`
  - Converts selected cells, entire rows, and entire columns into TSV/CSV/JSON strings.

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/ColumnFilterState.swift`
  - Defines structured column filters, descriptions, predicates, and active-state helpers.

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/ColumnFilterPopoverController.swift`
  - Native AppKit popover UI for categorical/date column filters.

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/InspectorCopyFormatter.swift`
  - Converts current inspector row/content state to TEXT or JSON.

### Modified Files

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/CsvTableView.swift`
  - Add drag tracking and modifier-aware cell hit reporting.

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
  - Integrate selection model, context menu commands, header filter UI, structured filters, inspector copy buttons, and saved-view state.

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/SortHeaderCell.swift`
  - Add optional filter-active/filter-available indicator and hit-region support.

- `NanumCsvViewerMac/Sources/NanumCsvViewerMac/CsvTableHeaderView.swift`
  - Preserve trailing filler behavior while supporting header filter affordance drawing.

- `NanumCsvViewerMac/Sources/CsvCore/VirtualCsvDocument.swift`
  - Add cancellable distinct-value collection helpers and date-range predicate support helpers if needed.

- `NanumCsvViewerMac/Sources/CsvCore/SavedCsvView.swift`
  - Persist structured column filters in saved views.

### Test Files

- `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`
- `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/SortHeaderCellTests.swift`
- `NanumCsvViewerMac/Tests/CsvCoreTests/VirtualCsvDocumentTests.swift`
- `NanumCsvViewerMac/Tests/CsvCoreTests/SavedCsvViewTests.swift`

---

## Data Model Design

### Grid Cell Coordinates

```swift
struct GridCellCoordinate: Hashable, Equatable {
    let row: Int          // display row index
    let column: Int       // data column index, not including row-number column
}
```

Rules:

- `column == 0` maps to the first CSV data column.
- The row-number gutter column is not selectable as data.
- Display row indices follow the current filtered/sorted view.

### Grid Selection Model

```swift
struct GridSelectionModel: Equatable {
    private(set) var selectedCells: Set<GridCellCoordinate> = []
    private(set) var anchor: GridCellCoordinate?

    var isEmpty: Bool { selectedCells.isEmpty }
    var selectedRows: IndexSet
    var selectedColumns: IndexSet

    mutating func replace(with cell: GridCellCoordinate)
    mutating func replace(with range: ClosedRangeGrid)
    mutating func toggle(_ cell: GridCellCoordinate)
    mutating func extend(to cell: GridCellCoordinate)
    mutating func clear()
    func contains(row: Int, column: Int) -> Bool
    func boundingRect() -> ClosedRangeGrid?
}
```

```swift
struct ClosedRangeGrid: Equatable {
    let rows: ClosedRange<Int>
    let columns: ClosedRange<Int>
}
```

### Column Filter State

```swift
enum ColumnFilter: Equatable, Codable {
    case selectedValues(column: Int, values: Set<String>, includeBlanks: Bool)
    case dateRange(column: Int, start: Date?, end: Date?)
}

struct ColumnFilterState: Equatable, Codable {
    var filters: [ColumnFilter] = []

    mutating func setValues(column: Int, values: Set<String>, includeBlanks: Bool)
    mutating func setDateRange(column: Int, start: Date?, end: Date?)
    mutating func remove(column: Int)
    func filter(for column: Int) -> ColumnFilter?
    func predicate(dateParser: CsvDateParsing) -> ([String]) -> Bool
    func descriptions(columnNames: [String]) -> [String]
}
```

`ColumnFilterState` replaces the ad-hoc `valueConditions` for new column filters. Existing `Filter by Cell` can be migrated to `selectedValues(column:values:)` so all value filters share one state path.

### Distinct Values

Add to `VirtualCsvDocument`:

```swift
public struct DistinctColumnValue: Equatable, Sendable {
    public let value: String
    public let count: Int
}

public func distinctValues(
    column: Int,
    withinCurrentView: Bool,
    limit: Int?,
    progress: ((Int) -> Void)?,
    cancellation: CancellationFlag
) throws -> [DistinctColumnValue]
```

Rules:

- Use current display view when `withinCurrentView == true`.
- Return values sorted by descending count, then localized/lexicographic value.
- Preserve blank value as `""`; UI displays it as `(Blank)` / `(빈 값)`.
- If `limit` is set and exceeded, return the first `limit` values plus a flag in a future `DistinctColumnValuesResult`. For the first implementation, use `limit: nil` for correctness unless performance requires a cap.

---

## Task 1: Grid Selection Model

**Files:**
- Create: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/GridSelectionModel.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Produces: `GridCellCoordinate`, `ClosedRangeGrid`, `GridSelectionModel`
- Consumed by later tasks: `MainWindowController`, `GridCopyFormatter`

- [ ] Add failing unit tests for single-cell selection.

Expected test shape:

```swift
func testGridSelectionReplacesWithSingleCell() {
    var model = GridSelectionModel()
    model.replace(with: GridCellCoordinate(row: 2, column: 1))

    XCTAssertTrue(model.contains(row: 2, column: 1))
    XCTAssertEqual(model.anchor, GridCellCoordinate(row: 2, column: 1))
    XCTAssertEqual(model.selectedCells.count, 1)
}
```

- [ ] Add failing unit tests for rectangular range selection.

```swift
func testGridSelectionExtendsFromAnchorToRectangle() {
    var model = GridSelectionModel()
    model.replace(with: GridCellCoordinate(row: 1, column: 1))
    model.extend(to: GridCellCoordinate(row: 3, column: 2))

    XCTAssertTrue(model.contains(row: 1, column: 1))
    XCTAssertTrue(model.contains(row: 3, column: 2))
    XCTAssertEqual(model.selectedCells.count, 6)
}
```

- [ ] Add failing unit tests for additive toggle.

```swift
func testGridSelectionTogglesCells() {
    var model = GridSelectionModel()
    let cell = GridCellCoordinate(row: 4, column: 2)

    model.toggle(cell)
    XCTAssertTrue(model.contains(row: 4, column: 2))

    model.toggle(cell)
    XCTAssertFalse(model.contains(row: 4, column: 2))
}
```

- [ ] Run `swift test --filter MainWindowControllerGridTests`.
- [ ] Implement the model with no AppKit dependencies.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

---

## Task 2: Integrate Multi-Cell Selection Into the Main Grid

**Files:**
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/CsvTableView.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Consumes: `GridSelectionModel`
- Produces testing helpers:
  - `selectGridCellForTesting(row:column:)`
  - `extendGridSelectionForTesting(toRow:column:)`
  - `toggleGridCellSelectionForTesting(row:column:)`
  - `selectedGridCellsForTesting`

- [ ] Add controller tests that verify click selection updates selected cell state.
- [ ] Add controller tests that verify shift-like extension selects a rectangle.
- [ ] Add controller tests that verify command-like toggle adds/removes cells.
- [ ] Update `CsvTableView` to report mouse down, drag, and mouse up events with row, column, and modifier flags.
- [ ] In `MainWindowController`, replace the single-cell-only state with `GridSelectionModel` while keeping `tableView.selectedRow` synchronized for existing selected-value and inspector behavior.
- [ ] Update `tableView(_:viewFor:row:)` so any selected data cell receives the same selected-cell background currently used for one cell.
- [ ] Keep row-number gutter non-selectable.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

Acceptance criteria:

- Existing single-cell selection behavior still updates selected value and inspector.
- Multiple selected cells render with visible highlight.
- Selecting a new plain cell clears the previous multi-selection.
- Changing filters, sorting, opening a new document, or closing the document clears grid selection.

---

## Task 3: Copy Selected Cells as TSV

**Files:**
- Create: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/GridCopyFormatter.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/NanumCsvViewerMac.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Produces:
  - `GridCopyFormatter.tsv(rows:headers:selection:) -> String`
  - `MainWindowController.copySelectedGridCells(_:)`

- [ ] Add formatter tests for rectangular selection.

```swift
func testGridCopyFormatterCopiesRectangleAsTsv() {
    let rows = [
        ["A1", "B1", "C1"],
        ["A2", "B2", "C2"]
    ]
    let selection: Set<GridCellCoordinate> = [
        .init(row: 0, column: 1),
        .init(row: 0, column: 2),
        .init(row: 1, column: 1),
        .init(row: 1, column: 2)
    ]

    XCTAssertEqual(GridCopyFormatter.tsv(rows: rows, selection: selection), "B1\tC1\nB2\tC2\n")
}
```

- [ ] Add formatter tests for sparse additive selection; empty cells inside the bounding rectangle become empty strings.
- [ ] Update app menu `Copy Cell` to route through `copySelectedGridCells(_:)`; keep existing single-cell copy methods for menu compatibility or make them wrappers.
- [ ] For selected cells, fetch display rows from `VirtualCsvDocument` by display row index.
- [ ] Put TSV on `NSPasteboard.general` using `.string`.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

Acceptance criteria:

- `Cmd+C` copies all selected cells as TSV.
- Single-cell copy remains compatible with the previous behavior.
- Copy output respects current filtered/sorted visible row order.

---

## Task 4: Context Menu Row and Column Copy

**Files:**
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Produces:
  - `copyEntireCurrentRow(_:)`
  - `copyEntireCurrentColumn(_:)`
  - `rowCopyStringForTesting(row:)`
  - `columnCopyStringForTesting(column:includeHeader:)`

- [ ] Add failing test for copying an entire visible row as TSV.
- [ ] Add failing test for copying an entire visible column as TSV with header.
- [ ] Add menu items in `configureContextMenu()`:
  - `Copy Entire Row` / `행 전체 복사`
  - `Copy Entire Column` / `열 전체 복사`
- [ ] Implement row copy using one `getDisplayRow(row)` call.
- [ ] Implement column copy using background operation for large data:
  - If `displayRowCount <= 50_000`, synchronous helper is acceptable for tests and small files.
  - For larger views, use `runViewOperation` or an equivalent cancellable background path.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

Acceptance criteria:

- Right-clicking a cell first updates `currentDataColumn` and selected row.
- Row copy excludes row-number gutter and includes visible data columns.
- Column copy includes the column header as the first line.
- Hidden columns are not included in row copy.

---

## Task 5: Inspector Copy TEXT and JSON

**Files:**
- Create: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/InspectorCopyFormatter.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/NanumCsvViewerMac.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Produces:
  - `InspectorContentKind`
  - `InspectorCopyFormatter.text(...)`
  - `InspectorCopyFormatter.jsonObject(headers:row:)`
  - `copyInspectorText(_:)`
  - `copyInspectorJson(_:)`

Suggested state:

```swift
enum InspectorContentKind: Equatable {
    case empty
    case row(displayRow: Int, sourceRow: Int64)
    case columnStatistics(column: Int)
    case performance
    case analysis
}
```

- [ ] Add failing tests for TEXT copy from selected row inspector.
- [ ] Add failing tests for JSON copy from selected row inspector.
- [ ] Add failing tests for duplicate column names in JSON. Keys should be made unique.
- [ ] Add buttons to the inspector header:
  - `Copy (TEXT)` / `복사(TEXT)`
  - `Copy (JSON)` / `복사(JSON)`
- [ ] Track current inspector content kind whenever `updateDetailPanel()`, `renderColumnStatistics()`, `showPerformanceDashboard()`, or analysis rendering changes the inspector.
- [ ] Enable JSON copy only for `.row`.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

Acceptance criteria:

- TEXT copy exactly matches visible inspector text for row/statistics/performance views.
- JSON copy for a row includes every CSV column with stable unique keys.
- Buttons disable when no copyable content exists.

---

## Task 6: Structured Column Filter State

**Files:**
- Create: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/ColumnFilterState.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Produces: `ColumnFilter`, `ColumnFilterState`
- Main controller owns: `private var columnFilterState = ColumnFilterState()`

- [ ] Add unit tests for `selectedValues` predicate.
- [ ] Add unit tests for filter descriptions.
- [ ] Add unit tests that multiple column filters combine with AND semantics.
- [ ] Replace new value-style filters with `ColumnFilterState`; keep existing text condition unchanged.
- [ ] Migrate `filterBySelectedCell(_:)` to add `selectedValues(column:values:)`.
- [ ] Update `combinedPredicate()` to check:
  1. text condition
  2. column filter state predicate
- [ ] Update `filterDescriptions()` and filter tokens to include column filters.
- [ ] Update `clearFilter(_:)`, document open/close, and restore paths to clear column filters.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

Acceptance criteria:

- Existing text filter still works.
- Existing `Filter by Cell` still works but is represented as structured column filter state.
- Multiple filters combine with AND semantics.
- Clear Filter removes text, value, categorical, and date filters.

---

## Task 7: Distinct Values Collection

**Files:**
- Modify: `NanumCsvViewerMac/Sources/CsvCore/VirtualCsvDocument.swift`
- Test: `NanumCsvViewerMac/Tests/CsvCoreTests/VirtualCsvDocumentTests.swift`

**Interfaces:**
- Produces:
  - `DistinctColumnValue`
  - `distinctValues(column:withinCurrentView:limit:progress:cancellation:)`

- [ ] Add failing test that distinct values are counted and sorted.
- [ ] Add failing test that blank values are counted.
- [ ] Add failing test that `withinCurrentView: true` respects current filters.
- [ ] Implement a cancellable scan over display rows or source rows.
- [ ] Report progress at regular intervals for large data.
- [ ] Run `swift test --filter VirtualCsvDocumentTests`.

Acceptance criteria:

- Result counts match current visible rows when `withinCurrentView == true`.
- Function throws cancellation when `CancellationFlag` is cancelled.
- Function does not mutate current view, sort order, or filters.

---

## Task 8: Header Filter Affordance and Popover

**Files:**
- Create: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/ColumnFilterPopoverController.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/SortHeaderCell.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/SortHeaderCellTests.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Produces:
  - `ColumnFilterPopoverController`
  - `ColumnFilterPopoverController.Selection`
  - header filter click routing in `MainWindowController`

- [ ] Add `SortHeaderCell` tests that filter indicator renders only when `filterAvailable == true`.
- [ ] Add test that active filter changes indicator state.
- [ ] Add controller test that categorical/date columns expose filter availability.
- [ ] Add hit testing for the filter icon area:
  - Header title/body click continues to sort.
  - Filter icon click opens popover and does not sort.
- [ ] Build popover layout:
  - Search field
  - Scrollable checkbox list
  - `Select All`, `Clear`
  - `Apply`, `Cancel`
- [ ] Load distinct values asynchronously with loading state.
- [ ] On apply, update `ColumnFilterState` and call `rebuildFilter(message:)`.
- [ ] Run `swift test --filter SortHeaderCellTests`.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

Acceptance criteria:

- Only categorical/date headers show filter affordance.
- Active filter state is visible on the header.
- Header sort remains intact.
- Popover does not block the main thread while collecting values.

---

## Task 9: Date Range Filter

**Files:**
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/ColumnFilterState.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/ColumnFilterPopoverController.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`
- Test: `NanumCsvViewerMac/Tests/CsvCoreTests/VirtualCsvDocumentTests.swift`

**Interfaces:**
- Produces:
  - `ColumnFilter.dateRange(column:start:end:)`
  - reusable date parsing helper if one is not already exposed

- [ ] Identify the current date parsing implementation used by column statistics.
- [ ] Extract or expose a shared parser so filters and type inference agree.
- [ ] Add tests for inclusive date range filtering.
- [ ] Add tests for open-ended start-only and end-only ranges.
- [ ] Add tests for invalid/unparseable dates. They should not match a range.
- [ ] Add date range controls to date filter popover.
- [ ] For the first implementation, make range filtering and checkbox value filtering mutually exclusive in the UI to avoid ambiguous semantics.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.
- [ ] Run `swift test --filter VirtualCsvDocumentTests`.

Acceptance criteria:

- Date range filter compares normalized dates.
- Date range is inclusive on both ends.
- Empty start/end means unbounded.
- Date filter token displays a readable range.

---

## Task 10: Save and Restore New Filter State

**Files:**
- Modify: `NanumCsvViewerMac/Sources/CsvCore/SavedCsvView.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Test: `NanumCsvViewerMac/Tests/CsvCoreTests/SavedCsvViewTests.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Extends: `SavedCsvView`
- Consumes: `ColumnFilterState`

- [ ] Add failing encode/decode tests for saved selected-values filter.
- [ ] Add failing encode/decode tests for saved date-range filter.
- [ ] Add `columnFilters: ColumnFilterState?` to `SavedCsvView`.
- [ ] On save, include current structured filters.
- [ ] On restore, restore structured filters, text filter, sort keys, hidden columns, and current column.
- [ ] Preserve backward compatibility with saved views that do not include `columnFilters`.
- [ ] Run `swift test --filter SavedCsvViewTests`.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

Acceptance criteria:

- Existing saved views still decode.
- New column filters survive save/restore.
- Restore applies filters before sort, matching current behavior.

---

## Task 11: Polish, Menu Validation, and Feature State

**Files:**
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/MainWindowController.swift`
- Modify: `NanumCsvViewerMac/Sources/NanumCsvViewerMac/NanumCsvViewerMac.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/AppMenuTests.swift`
- Test: `NanumCsvViewerMac/Tests/NanumCsvViewerMacTests/MainWindowControllerGridTests.swift`

**Interfaces:**
- Updates existing `validateMenuItem(_:)`
- Updates existing `updateFeatureState()`

- [ ] Add menu tests for new commands.
- [ ] Disable copy commands when no document or no selected cell/range exists.
- [ ] Disable column filter affordance until column statistics are available.
- [ ] Disable date range controls for non-date columns.
- [ ] Update toolbar/menu icons if needed.
- [ ] Confirm all new menu labels have Korean and English text via `L.t`.
- [ ] Run `swift test --filter AppMenuTests`.
- [ ] Run `swift test --filter MainWindowControllerGridTests`.

Acceptance criteria:

- Menus are disabled when commands cannot run.
- Busy state disables operations that would scan or mutate the view.
- UI copy uses consistent naming:
  - `Copy Selected Cells`
  - `Copy Entire Row`
  - `Copy Entire Column`
  - `Copy Inspector as Text`
  - `Copy Inspector as JSON`

---

## Task 12: Final Verification

**Files:**
- Test only.

- [ ] Run targeted tests:

```bash
swift test --filter MainWindowControllerGridTests
swift test --filter SortHeaderCellTests
swift test --filter VirtualCsvDocumentTests
swift test --filter SavedCsvViewTests
swift test --filter AppMenuTests
```

- [ ] Run full test suite:

```bash
swift test
```

- [ ] Run diff whitespace check:

```bash
git diff --check
```

- [ ] Build the app:

```bash
Scripts/build-app.sh
```

- [ ] Manual smoke test:
  - Open a CSV.
  - Drag-select a 2x2 cell range and paste into a text editor.
  - Use `Shift + click` to extend a range.
  - Use `Command + click` to toggle a cell.
  - Right-click a cell and copy entire row.
  - Right-click a cell and copy entire column.
  - Open inspector and copy TEXT.
  - Open inspector and copy JSON for a selected row.
  - Open a categorical header filter, select two values, apply it.
  - Open a date header filter, apply a date range.
  - Clear filters and verify all rows return.

---

## Risks and Mitigations

### Large Distinct Value Lists

Risk: A high-cardinality categorical column can produce thousands of checkbox rows.

Mitigation:

- Load values asynchronously.
- Add search.
- Virtualize the checkbox list if performance becomes poor.
- Consider an initial soft cap with "Show more" only after correctness is working.

### Header Click Conflict

Risk: Existing header click sorting conflicts with filter icon click.

Mitigation:

- Define a fixed filter icon hit rect in `SortHeaderCell`.
- Route filter-icon clicks before `tableView(_:didClick:)` sorting.
- Add tests for "filter click does not sort" and "title click sorts".

### Date Parsing Consistency

Risk: Date filter matching can disagree with type inference.

Mitigation:

- Reuse the same parser used by column statistics.
- Add tests for all date formats already covered by `VirtualCsvDocumentTests`.

### MainWindowController Size

Risk: The controller is already large, and adding all features inline will make it harder to maintain.

Mitigation:

- Keep state/formatting in new focused files.
- Keep `MainWindowController` as the UI integration layer only.
- Avoid moving unrelated existing behavior in this feature.

### Selection Versus Row-Based Inspector

Risk: The inspector currently assumes one selected row/current column.

Mitigation:

- Keep a primary cell as selection anchor.
- Inspector uses the anchor row and anchor column.
- Multi-cell selection is for copy and visual selection only in the first version.

## Out of Scope for First Implementation

- Formula-style filters.
- Numeric slider/range filters for integer/float columns.
- Persisting arbitrary multi-cell selections.
- Clipboard HTML table format.
- Drag-to-fill or editing cell values.
- Multi-column OR filter groups.
- Full Excel AutoFilter parity.

## Suggested PR / Commit Breakdown

1. `feat: add grid selection model`
2. `feat: support multi-cell grid copy`
3. `feat: add row and column copy commands`
4. `feat: add inspector copy actions`
5. `feat: add structured column filter state`
6. `feat: collect distinct column values`
7. `feat: add categorical header filters`
8. `feat: add date range header filters`
9. `feat: persist column filters in saved views`
10. `test: cover main grid enhancement workflow`

## Completion Criteria

- All requested user-facing features are implemented.
- Existing single-cell copy, text filter, filter by cell, sorting, hidden columns, inspector, and saved view behavior still works.
- Targeted tests and full `swift test` pass.
- `git diff --check` passes.
- The app builds with `Scripts/build-app.sh`.
