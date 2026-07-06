import { Link, useSearchParams } from 'react-router-dom';
import { Button } from '@/components/beacon';

export default function ErrorPage() {
  const [params] = useSearchParams();
  const requestId = params.get('requestId') || params.get('rid');

  return (
    <div className="min-h-screen grid place-items-center p-6 bg-bg">
      <div className="w-full max-w-md bg-surface border border-border rounded-lg shadow-pop p-8 text-center">
        <h1 className="text-xl font-semibold text-text m-0 mb-2">Something went wrong</h1>
        <p className="text-text-muted m-0">An unexpected error occurred while processing your request.</p>
        {requestId && (
          <p className="text-text-muted mono text-xs mt-3 m-0">
            Request id: <strong>{requestId}</strong>
          </p>
        )}
        <Link to="/home" className="block mt-4">
          <Button variant="primary" className="w-full justify-center">Back to home</Button>
        </Link>
      </div>
    </div>
  );
}
