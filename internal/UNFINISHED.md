# Unfinished Features

Tracker for features that exist in the codebase but are intentionally hidden from users pending redesign / completion.

## Dashboards

**Status:** hidden from sidebar 2026-05-12. Will be redesigned before re-enabling.

**Surface area still in the repo:**
- Routes: `/dashboards`, `/dashboards/:id`, `/dashboards/:id/edit` (registered in `src/Beacon.UI/web/src/App.tsx`)
- Pages: `src/Beacon.UI/web/src/routes/dashboards/{DashboardsListPage,DashboardViewerPage,DashboardBuilderPage}.tsx`
- API: `/beacon/api/dashboards/*` (`src/Beacon.Api/Endpoints/DashboardsEndpoints.cs`)
- Handlers: `src/Beacon.Core/Handlers/Dashboards/`
- Entities + migrations remain in place (no DB cleanup yet — preserve user data).

**What was hidden:**
- Sidebar entry in `src/Beacon.UI/web/src/components/layout/Sidebar.tsx` (Overview section).

**Why hidden:**
- Current UX isn't useful in its present form. Decision: re-plan the feature before exposing it again rather than ship more iteration on the existing design.

**Before re-enabling:**
- Reach a new product spec.
- Restore the sidebar nav item.
- Verify routes still resolve and codegen is up to date.
