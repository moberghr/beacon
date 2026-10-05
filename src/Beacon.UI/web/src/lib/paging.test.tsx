import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useLocation } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { http, HttpResponse } from "msw";
import { mswServer } from "../../vitest.setup";
import { renderWithProviders } from "@/test/render";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Pager, pageWindow } from "@/components/data/Pager";
import {
  listQueryString,
  parseSortParam,
  sortToParam,
  toggleSort,
} from "./paging";
import { usePagedList } from "./usePagedList";

describe("sort params", () => {
  it("round-trips the server sort syntax", () => {
    expect(sortToParam({ column: "createdTime", direction: "desc" })).toBe(
      "-createdTime",
    );
    expect(parseSortParam("-createdTime,name")).toEqual({
      column: "createdTime",
      direction: "desc",
    });
    expect(parseSortParam("name")).toEqual({
      column: "name",
      direction: "asc",
    });
    expect(parseSortParam("")).toBeNull();
  });

  it("toggles the same column and starts a new column ascending", () => {
    expect(toggleSort({ column: "name", direction: "asc" }, "name")).toEqual({
      column: "name",
      direction: "desc",
    });
    expect(toggleSort({ column: "name", direction: "desc" }, "score")).toEqual({
      column: "score",
      direction: "asc",
    });
  });

  it("leaves empty values out of the query string", () => {
    expect(
      listQueryString({
        page: 0,
        search: "",
        status: null,
        sort: undefined,
        ok: false,
      }),
    ).toBe("?page=0&ok=false");
  });
});

describe("Pager", () => {
  it("collapses long ranges around the current page", () => {
    expect(pageWindow(0, 1)).toEqual([0]);
    expect(pageWindow(5, 20)).toEqual([0, "gap", 4, 5, 6, "gap", 19]);
    expect(pageWindow(2, 5)).toEqual([0, 1, 2, 3, 4]);
  });

  it("shows the visible range and moves by zero-based pages", () => {
    const pages: number[] = [];
    render(
      <Pager
        page={1}
        pageSize={20}
        pageCount={3}
        totalCount={45}
        onPageChange={(x) => pages.push(x)}
      />,
    );

    expect(screen.getByText("21–40 of 45")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Page 2" })).toHaveAttribute(
      "aria-current",
      "page",
    );
    fireEvent.click(screen.getByRole("button", { name: "Next page" }));
    fireEvent.click(screen.getByRole("button", { name: "Page 1" }));
    expect(pages).toEqual([2, 0]);
  });

  it("renders nothing for an empty list", () => {
    const { container } = render(
      <Pager
        page={0}
        pageSize={20}
        pageCount={0}
        totalCount={0}
        onPageChange={() => {}}
      />,
    );
    expect(container).toBeEmptyDOMElement();
  });
});

interface Row {
  id: number;
  name: string;
}

const COLUMNS: Column<Row>[] = [
  { key: "name", header: "Name", sortKey: "name", render: (x) => x.name },
  { key: "id", header: "Id", render: (x) => x.id },
];

describe("DataTable sorting", () => {
  it("marks the active sort column and reports header clicks", () => {
    const clicked: string[] = [];
    render(
      <DataTable
        columns={COLUMNS}
        rows={[{ id: 1, name: "a" }]}
        rowKey={(x) => x.id}
        gridTemplate="1fr 1fr"
        sort={{ column: "NAME", direction: "desc" }}
        onSortChange={(x) => clicked.push(x)}
      />,
    );

    expect(screen.getAllByRole("columnheader")[0]).toHaveAttribute(
      "aria-sort",
      "descending",
    );
    expect(screen.queryByRole("button", { name: /^id/i })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /name/i }));
    expect(clicked).toEqual(["name"]);
  });
});

function Harness() {
  const list = usePagedList<Row, { search: string }>({
    queryKey: ["rows"],
    path: "/beacon/api/rows",
    filters: { search: "" },
    defaultSort: { column: "name", direction: "asc" },
  });
  const location = useLocation();

  return (
    <>
      <span data-testid="url">{location.search}</span>
      <input
        aria-label="search"
        value={list.filters.search}
        onChange={(e) => list.setFilter("search", e.target.value)}
      />
      <DataTable
        columns={COLUMNS}
        rows={list.items}
        rowKey={(x) => x.id}
        gridTemplate="1fr 1fr"
        {...list.tableProps}
      />
    </>
  );
}

describe("usePagedList", () => {
  it("sends page, sort and filters to the server and keeps them in the URL", async () => {
    const requests: string[] = [];
    mswServer.use(
      http.get("*/beacon/api/rows", ({ request }) => {
        const url = new URL(request.url);
        requests.push(url.search);
        const page = Number(url.searchParams.get("page"));
        return HttpResponse.json({
          items: [{ id: page + 1, name: `row ${page}` }],
          totalCount: 45,
          pageCount: 3,
        });
      }),
    );

    renderWithProviders(<Harness />, { initialEntries: ["/?page=2"] });

    expect(await screen.findByText("row 1")).toBeInTheDocument();
    expect(requests[0]).toBe("?page=1&pageSize=20&sort=name");

    fireEvent.click(screen.getByRole("button", { name: /name/i }));
    await waitFor(() =>
      expect(screen.getByTestId("url")).toHaveTextContent("?sort=-name"),
    );
    expect(await screen.findByText("row 0")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Next page" }));
    await waitFor(() =>
      expect(screen.getByTestId("url")).toHaveTextContent("page=2"),
    );

    fireEvent.change(screen.getByRole("textbox", { name: "search" }), {
      target: { value: "abc" },
    });
    await waitFor(() =>
      expect(requests.at(-1)).toBe("?search=abc&page=0&pageSize=20&sort=-name"),
    );
    expect(screen.getByTestId("url")).not.toHaveTextContent("page=");
  });

  it("steps back when the page is past the end", async () => {
    mswServer.use(
      http.get("*/beacon/api/rows", ({ request }) => {
        const page = Number(new URL(request.url).searchParams.get("page"));
        return HttpResponse.json({
          items: page < 2 ? [{ id: 1, name: `row ${page}` }] : [],
          totalCount: 21,
          pageCount: 2,
        });
      }),
    );

    renderWithProviders(<Harness />, { initialEntries: ["/?page=9"] });

    await waitFor(() =>
      expect(screen.getByTestId("url")).toHaveTextContent("?page=2"),
    );
    expect(await screen.findByText("row 1")).toBeInTheDocument();
  });
});
