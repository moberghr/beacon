import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type {
  PreviewRow,
  QueryPreviewResult,
  QueryPreviewStep,
  QueryResultPage,
} from "../queries";
import { QueryRunPanel } from "./QueryRunPanel";
import { rowsToTsv } from "./ResultsTable";

const ROWS: PreviewRow[] = [
  { LoanId: 4866455, Capital: 100000, RefundsAmount: null },
  { LoanId: 5078261, Capital: 54995, RefundsAmount: null },
];

function step(overrides: Partial<QueryPreviewStep> = {}): QueryPreviewStep {
  return {
    stepOrder: 1,
    stepName: "Step 1",
    dataSourceName: "Netgiro PROD",
    databaseEngine: "MSSQL",
    success: true,
    errorMessage: null,
    executionTimeMs: 13031,
    totalRows: ROWS.length,
    previewRows: ROWS,
    ...overrides,
  };
}

function page(overrides: Partial<QueryResultPage> = {}): QueryResultPage {
  return {
    rows: ROWS,
    totalCount: ROWS.length,
    pageCount: 1,
    page: 0,
    pageSize: 20,
    sortable: true,
    sort: null,
    ...overrides,
  };
}

function result(
  overrides: Partial<QueryPreviewResult> = {},
): QueryPreviewResult {
  return {
    success: true,
    errorMessage: null,
    totalExecutionTimeMs: 13031,
    dataSourcesInvolved: ["Netgiro PROD"],
    steps: [step()],
    result: page(),
    ...overrides,
  };
}

function renderPanel(props: Partial<Parameters<typeof QueryRunPanel>[0]> = {}) {
  const handlers = {
    onClose: vi.fn(),
    onRerun: vi.fn(),
    onPageChange: vi.fn(),
    onSortChange: vi.fn(),
  };
  render(
    <QueryRunPanel
      running={false}
      result={result()}
      requestError={null}
      sort={null}
      {...handlers}
      {...props}
    />,
  );
  return handlers;
}

describe("QueryRunPanel", () => {
  it("shows the rows, count, duration and data source of a successful run", () => {
    renderPanel();

    const panel = screen.getByRole("region", { name: /query run result/i });
    expect(within(panel).getByText("2 rows")).toBeInTheDocument();
    expect(within(panel).getByText("13.0 s")).toBeInTheDocument();
    expect(within(panel).getByText("Netgiro PROD")).toBeInTheDocument();
    expect(within(panel).getByText("4866455")).toBeInTheDocument();
    expect(
      within(panel).getByText(/not recorded as an execution/i),
    ).toBeInTheDocument();
  });

  it("pages on the server: shows the range, continues row numbers and asks for the next page", () => {
    const { onPageChange } = renderPanel({
      result: result({
        result: page({ totalCount: 4213, pageCount: 211, page: 2 }),
      }),
    });

    expect(screen.getByText("4,213 rows")).toBeInTheDocument();
    expect(screen.getByText("41–60 of 4,213")).toBeInTheDocument();
    expect(screen.getByText("41")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Next page" }));
    expect(onPageChange).toHaveBeenCalledWith(3);
  });

  it("sorts by a result column on the server when the query allows it", () => {
    const { onSortChange } = renderPanel({
      sort: { column: "Capital", direction: "desc" },
    });

    expect(
      screen.getByRole("columnheader", { name: /capital/i }),
    ).toHaveAttribute("aria-sort", "descending");
    fireEvent.click(screen.getByRole("button", { name: /loanid/i }));
    expect(onSortChange).toHaveBeenCalledWith("LoanId");
  });

  it("turns off sorting and says why when the SQL shape cannot be sorted", () => {
    renderPanel({ result: result({ result: page({ sortable: false }) }) });

    expect(screen.queryByRole("button", { name: /loanid/i })).toBeNull();
    expect(screen.getByText(/cannot be sorted by column/i)).toBeInTheDocument();
  });

  it("shows the failing step error when the query failed", () => {
    renderPanel({
      result: result({
        success: false,
        errorMessage: "Invalid column name 'Capitall'.",
        result: null,
        steps: [
          step({
            success: false,
            errorMessage: "Invalid column name 'Capitall'.",
            previewRows: [],
          }),
        ],
      }),
    });

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Invalid column name 'Capitall'.",
    );
    expect(screen.queryByText("4866455")).toBeNull();
  });

  it("shows the request error when the call itself failed", () => {
    renderPanel({ result: null, requestError: "Query preview failed (500)" });

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Query preview failed (500)",
    );
  });

  it("keeps the current page visible, dimmed, while the next one loads", () => {
    renderPanel({ running: true });

    expect(screen.getByText("Running query…")).toBeInTheDocument();
    expect(screen.getByText("4866455")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /re-run/i })).toBeDisabled();
  });

  it("shows a multi-step query as the paged final result plus each step’s first rows", () => {
    renderPanel({
      result: result({
        steps: [
          step({
            stepOrder: 1,
            previewRows: [{ Step: "one" }],
            totalRows: 500,
          }),
          step({ stepOrder: 2, previewRows: [{ Step: "two" }], totalRows: 1 }),
        ],
        result: page({ rows: [{ Step: "final" }], totalCount: 1 }),
      }),
    });

    expect(screen.getByText("final")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("radio", { name: "Step 1" }));
    expect(screen.getByText("one")).toBeInTheDocument();
    expect(
      screen.getByText(/Steps run in full to feed the final query/),
    ).toBeInTheDocument();
  });

  it("closes on Escape and re-runs on demand", () => {
    const { onClose, onRerun } = renderPanel();

    fireEvent.click(screen.getByRole("button", { name: /re-run/i }));
    fireEvent.keyDown(
      screen.getByRole("region", { name: /query run result/i }),
      { key: "Escape" },
    );

    expect(onRerun).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("copies the shown rows as tab-separated text", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText },
      configurable: true,
    });
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: /copy/i }));

    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith(rowsToTsv(ROWS)),
    );
    expect(await screen.findByText("Copied")).toBeInTheDocument();
  });
});

describe("rowsToTsv", () => {
  it("writes a header row and flattens tabs and newlines inside cells", () => {
    expect(
      rowsToTsv([
        { A: 1, B: "x\ty\nz" },
        { A: null, B: "ok" },
      ]),
    ).toBe("A\tB\n1\tx y z\nNULL\tok");
  });
});
