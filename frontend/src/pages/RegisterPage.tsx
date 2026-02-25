import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useAuthStore } from '../stores/authStore';

export default function RegisterPage() {
  const [inviteCode, setInviteCode] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const register = useAuthStore((s) => s.register);
  const navigate = useNavigate();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');

    if (password !== confirmPassword) {
      setError('Passwords do not match');
      return;
    }
    if (password.length < 6) {
      setError('Password must be at least 6 characters');
      return;
    }

    setLoading(true);
    try {
      await register(inviteCode, username, password);
      navigate('/');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg || 'Registration failed');
    } finally {
      setLoading(false);
    }
  };

  const inputClass = 'w-full bg-bg-primary border border-border rounded-lg px-4 py-2.5 text-text-primary text-sm focus:outline-none focus:ring-2 focus:ring-accent-blue/40 focus:border-accent-blue transition-all';

  return (
    <div className="min-h-screen flex items-center justify-center bg-bg-primary">
      <div className="w-full max-w-sm">
        <div className="bg-bg-secondary rounded-2xl border border-border p-8 shadow-2xl">
          <div className="flex justify-center mb-6">
            <div className="w-12 h-12 rounded-xl bg-accent-blue flex items-center justify-center">
              <svg className="w-7 h-7 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 6v12m-3-2.818l.879.659c1.171.879 3.07.879 4.242 0 1.172-.879 1.172-2.303 0-3.182C13.536 12.219 12.768 12 12 12c-.725 0-1.45-.22-2.003-.659-1.106-.879-1.106-2.303 0-3.182s2.9-.879 4.006 0l.415.33M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
            </div>
          </div>
          <div className="text-center mb-6">
            <h1 className="text-xl font-bold text-text-primary">Create Account</h1>
            <p className="text-text-secondary text-sm mt-1">Register with an invite code</p>
          </div>

          <form onSubmit={handleSubmit} className="space-y-4">
            {error && (
              <div className="bg-accent-red/10 border border-accent-red/20 text-accent-red text-sm px-4 py-2.5 rounded-lg">
                {error}
              </div>
            )}

            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Invite Code</label>
              <input
                type="text"
                value={inviteCode}
                onChange={(e) => setInviteCode(e.target.value.toUpperCase())}
                className={`${inputClass} font-mono tracking-wider uppercase`}
                placeholder="ABCD1234"
                required
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Username</label>
              <input type="text" value={username} onChange={(e) => setUsername(e.target.value)} className={inputClass} placeholder="Choose a username" required />
            </div>

            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Password</label>
              <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} className={inputClass} placeholder="Min. 6 characters" required />
            </div>

            <div>
              <label className="block text-sm font-medium text-text-primary mb-1.5">Confirm Password</label>
              <input type="password" value={confirmPassword} onChange={(e) => setConfirmPassword(e.target.value)} className={inputClass} placeholder="Repeat password" required />
            </div>

            <button
              type="submit"
              disabled={loading}
              className="w-full bg-accent-blue hover:bg-accent-blue/90 text-white font-medium py-2.5 px-4 rounded-lg text-sm transition-colors shadow-lg shadow-accent-blue/25 disabled:opacity-50 disabled:shadow-none"
            >
              {loading ? 'Registering...' : 'Register'}
            </button>
          </form>

          <p className="text-center mt-5 text-sm text-text-secondary">
            Already have an account?{' '}
            <Link to="/login" className="text-accent-blue hover:underline font-medium">
              Sign in
            </Link>
          </p>
        </div>
      </div>
    </div>
  );
}
