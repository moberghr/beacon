import type { ReactNode } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { ArrowRight, Lock } from 'lucide-react';
import { Card, CardBody, PageHeader } from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import { useAccess } from './useAuth';
import { opensWithoutProjectData, pagesOpenedBy } from './access';

/**
 * For a user whose roles are functional only (no Viewer or Editor role), every page but those their permissions open
 * would answer 403: such a page shows {@link FunctionalOnlyNotice} instead. Everyone else gets the page.
 */
export function FunctionalOnlyGate({ children }: { children: ReactNode }) {
  const access = useAccess();
  const { pathname } = useLocation();

  if (access.isFunctionalOnly && !opensWithoutProjectData(access, pathname)) {
    return <FunctionalOnlyNotice />;
  }

  return <>{children}</>;
}

/** Explains that project data needs a Viewer or Editor role, and links the pages the user's permissions open. */
export function FunctionalOnlyNotice() {
  const access = useAccess();
  const pages = pagesOpenedBy(access);

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader eyebrow="Access" prefix="Project" emphasis="data" />
      <EmptyState
        icon={<Lock />}
        title="Project data needs a Viewer or Editor role"
        description={
          'Your roles open the administrative pages listed below. To see projects, data sources, queries and the ' +
          'rest of their data, ask a Beacon administrator for a Viewer or Editor role as well.'
        }
      />
      {pages.length > 0 && (
        <Card>
          <CardBody>
            <h3 className="m-0 mb-2 text-sm font-semibold text-text">Pages you can open</h3>
            <ul className="m-0 p-0 list-none flex flex-col gap-1.5">
              {pages.map(x => (
                <li key={x.path}>
                  <Link to={x.path} className="inline-flex items-center gap-1.5 text-sm font-medium text-brand-600">
                    <ArrowRight className="size-3.5" />
                    {x.label}
                  </Link>
                </li>
              ))}
            </ul>
          </CardBody>
        </Card>
      )}
    </div>
  );
}
