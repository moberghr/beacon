import { useSearchParams } from 'react-router-dom';
import { AUTH_STYLES } from './LoginPage';

export default function ErrorPage() {
  const [params] = useSearchParams();
  const requestId = params.get('requestId') || params.get('rid');

  return (
    <div className="auth-shell">
      <div className="auth-card" style={{ textAlign: 'center' }}>
        <h1 className="auth-title">Something went wrong</h1>
        <p className="muted">An unexpected error occurred while processing your request.</p>
        {requestId && (
          <p className="muted mono" style={{ fontSize: 12, marginTop: 12 }}>
            Request id: <strong>{requestId}</strong>
          </p>
        )}
        <a className="btn btn--primary" href="/app/home" style={{ width: '100%', justifyContent: 'center', marginTop: 16 }}>
          Back to home
        </a>
      </div>
      <style>{AUTH_STYLES}</style>
    </div>
  );
}
