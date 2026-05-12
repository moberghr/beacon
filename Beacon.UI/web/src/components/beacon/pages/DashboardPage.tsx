/**
 * Sample dashboard — wires BeaconHero + KPIs + a sample card grid.
 *
 * Reference composition for the new Beacon design system. The real app
 * should replace the hard-coded numbers with live data.
 */
import { Download, RefreshCw, Search, Bolt, Activity, Clock, Bell } from 'lucide-react';

import {
  BeaconHero,
  Button,
  KPI,
  KPIGrid,
  Card,
  CardHead,
  CardTitle,
  CardSub,
  CardActions,
  CardBody,
  Pill,
  Banner,
} from '@/components/beacon';

export function DashboardPage() {
  return (
    <div className="flex flex-col gap-5 p-7">
      <BeaconHero
        user="mirko"
        actions={
          <>
            <Button variant="ghost" icon={<Download />}>
              Export
            </Button>
            <Button icon={<RefreshCw />}>Refresh</Button>
          </>
        }
      />

      <Banner
        tone="warn"
        icon={<Activity />}
        title="2 queries breached their SLA in the last 24 hours"
        sub={
          <>
            <span className="mono">qeh select</span> and{' '}
            <span className="mono">stale-payments</span> · awaiting review
          </>
        }
        actions={<Button size="sm">Review queue</Button>}
      />

      <KPIGrid>
        <KPI dot="brand" label="Queries" value="48" sub="3 added this week" />
        <KPI
          dot="ok"
          label="Executions 24h"
          value="2,891"
          sub={
            <>
              p50 <span className="mono">11 ms</span>
            </>
          }
        />
        <KPI dot="warn" label="Open tasks" value="7" sub={<Pill tone="warn">2 high</Pill>} />
        <KPI dot="info" label="Subscribers" value="124" sub="across 12 projects" />
      </KPIGrid>

      <div className="grid gap-5 lg:grid-cols-2">
        <Card>
          <CardHead>
            <Bolt className="size-3.5 text-text-muted" />
            <CardTitle>Recent executions</CardTitle>
            <CardSub>last 60 min</CardSub>
            <CardActions>
              <Button variant="ghost" size="sm" icon={<Search />}>
                Search
              </Button>
            </CardActions>
          </CardHead>
          <CardBody className="text-sm text-text-muted">
            Wire a small list/table here using your data layer.
          </CardBody>
        </Card>

        <Card>
          <CardHead>
            <Clock className="size-3.5 text-text-muted" />
            <CardTitle>Upcoming schedules</CardTitle>
            <CardActions>
              <Button variant="ghost" size="sm" icon={<Bell />}>
                Subscribe
              </Button>
            </CardActions>
          </CardHead>
          <CardBody className="text-sm text-text-muted">Cron preview goes here.</CardBody>
        </Card>
      </div>
    </div>
  );
}

export default DashboardPage;
