import { Component, type ReactNode } from 'react';
import { AlertTriangle, RotateCcw } from 'lucide-react';
import { Button, Card, CardBody } from '@/components/beacon';

interface Props {
  children: ReactNode;
}

interface State {
  error: Error | null;
}

export class RouteErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error): void {
    console.error('[RouteErrorBoundary]', error);
  }

  render() {
    if (!this.state.error) {
      return this.props.children;
    }

    return (
      <div className="flex flex-col items-center justify-center p-7 min-h-[60vh]">
        <Card className="max-w-md w-full">
          <CardBody>
            <div className="flex items-start gap-3">
              <AlertTriangle className="text-warn shrink-0 mt-0.5" size={22} aria-hidden />
              <div className="min-w-0">
                <h2 className="font-semibold text-base mb-1">Couldn't load this page</h2>
                <p className="text-text-muted text-sm mb-4">
                  Something went wrong while loading. This usually clears up with a reload.
                </p>
                <Button
                  onClick={() => {
                    sessionStorage.removeItem('beacon:lazy-reload-attempted');
                    window.location.reload();
                  }}
                >
                  <RotateCcw size={14} aria-hidden />
                  Reload
                </Button>
              </div>
            </div>
          </CardBody>
        </Card>
      </div>
    );
  }
}
