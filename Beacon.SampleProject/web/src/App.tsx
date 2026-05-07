import { useAuth } from './auth/useAuth';

export default function App() {
  const { data, isLoading, isError } = useAuth();

  if (isLoading) {
    return (
      <div className="flex h-full items-center justify-center text-slate-500">
        Loading…
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex h-full items-center justify-center text-red-600">
        Failed to load authentication state.
      </div>
    );
  }

  if (!data?.isAuthenticated) {
    if (typeof window !== 'undefined') {
      window.location.href = '/beacon';
    }
    return null;
  }

  return (
    <div className="flex h-full items-center justify-center">
      <div className="rounded-lg border border-slate-200 bg-white p-6 shadow-sm">
        <p className="text-sm text-slate-500">Beacon (React shell)</p>
        <h1 className="mt-1 text-xl font-semibold text-slate-900">
          Signed in as {data.displayName ?? data.email ?? data.userId}
        </h1>
        {data.roles && data.roles.length > 0 && (
          <p className="mt-2 text-sm text-slate-600">
            Roles: {data.roles.join(', ')}
          </p>
        )}
      </div>
    </div>
  );
}
