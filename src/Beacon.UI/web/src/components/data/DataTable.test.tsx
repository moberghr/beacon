import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { DataTable, type Column } from './DataTable';

interface Row {
  id: number;
  name: string;
}

const rows: Row[] = [
  { id: 1, name: 'Alpha' },
  { id: 2, name: 'Beta' },
];

const columns: Column<Row>[] = [
  { key: 'name', header: 'Name', render: r => r.name },
  { key: 'id', header: 'Id', render: r => String(r.id) },
];

describe('DataTable accessibility', () => {
  it('exposes table/row/columnheader/cell roles with an accessible name', () => {
    render(
      <DataTable
        columns={columns}
        rows={rows}
        rowKey={r => r.id}
        gridTemplate="1fr 1fr"
        ariaLabel="Widgets"
      />,
    );

    expect(screen.getByRole('table', { name: 'Widgets' })).toBeInTheDocument();
    expect(screen.getAllByRole('columnheader')).toHaveLength(2);
    // header row + 2 body rows
    expect(screen.getAllByRole('row')).toHaveLength(3);
    expect(screen.getAllByRole('cell')).toHaveLength(4);
  });

  it('activates a clickable row with Enter and Space', () => {
    const onRowClick = vi.fn();
    render(
      <DataTable
        columns={columns}
        rows={rows}
        rowKey={r => r.id}
        gridTemplate="1fr 1fr"
        onRowClick={onRowClick}
      />,
    );

    const bodyRows = screen.getAllByRole('row').slice(1);
    bodyRows[0].focus();
    fireEvent.keyDown(bodyRows[0], { key: 'Enter' });
    fireEvent.keyDown(bodyRows[0], { key: ' ' });
    expect(onRowClick).toHaveBeenCalledTimes(2);
    expect(onRowClick).toHaveBeenCalledWith(rows[0]);
  });

  it('does not fire onRowClick for clicks on interactive cell content', () => {
    const onRowClick = vi.fn();
    const onCellAction = vi.fn();
    const columnsWithAction: Column<Row>[] = [
      ...columns,
      {
        key: 'action',
        header: 'Action',
        render: () => (
          <button type="button" onClick={onCellAction}>
            Delete
          </button>
        ),
      },
    ];
    render(
      <DataTable
        columns={columnsWithAction}
        rows={rows}
        rowKey={r => r.id}
        gridTemplate="1fr 1fr 1fr"
        onRowClick={onRowClick}
      />,
    );

    fireEvent.click(screen.getAllByRole('button', { name: 'Delete' })[0]);
    expect(onCellAction).toHaveBeenCalledTimes(1);
    expect(onRowClick).not.toHaveBeenCalled();

    fireEvent.click(screen.getByText('Alpha'));
    expect(onRowClick).toHaveBeenCalledTimes(1);
    expect(onRowClick).toHaveBeenCalledWith(rows[0]);
  });
});
