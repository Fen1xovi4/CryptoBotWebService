import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { useAuthStore } from './stores/authStore';
import MainLayout from './components/Layout/MainLayout';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import DashboardPage from './pages/DashboardPage';
import AccountsPage from './pages/AccountsPage';
import AccountDetailPage from './pages/AccountDetailPage';
import StrategiesPage from './pages/StrategiesPage';
import ActiveBotsPage from './pages/ActiveBotsPage';
import TradeHistoryPage from './pages/TradeHistoryPage';
import SettingsPage from './pages/SettingsPage';
import TesterPage from './pages/TesterPage';
import ProxiesPage from './pages/ProxiesPage';
import UsersPage from './pages/UsersPage';
import InviteCodesPage from './pages/InviteCodesPage';
import WorkspaceDetailPage from './pages/WorkspaceDetailPage';

function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  if (!isAuthenticated) return <Navigate to="/login" replace />;
  return <>{children}</>;
}

function AdminRoute({ children }: { children: React.ReactNode }) {
  const role = useAuthStore((s) => s.role);
  if (role !== 'Admin') return <Navigate to="/" replace />;
  return <>{children}</>;
}

function AdminOrManagerRoute({ children }: { children: React.ReactNode }) {
  const role = useAuthStore((s) => s.role);
  if (role !== 'Admin' && role !== 'Manager') return <Navigate to="/" replace />;
  return <>{children}</>;
}

export default function App() {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);

  return (
    <BrowserRouter>
      <Routes>
        <Route
          path="/login"
          element={isAuthenticated ? <Navigate to="/" replace /> : <LoginPage />}
        />
        <Route
          path="/register"
          element={isAuthenticated ? <Navigate to="/" replace /> : <RegisterPage />}
        />
        <Route
          element={
            <ProtectedRoute>
              <MainLayout />
            </ProtectedRoute>
          }
        >
          <Route path="/" element={<DashboardPage />} />
          <Route path="/workspace/:id" element={<WorkspaceDetailPage />} />
          <Route path="/proxies" element={<ProxiesPage />} />
          <Route path="/accounts" element={<AccountsPage />} />
          <Route path="/accounts/:id" element={<AccountDetailPage />} />
          <Route path="/strategies" element={<StrategiesPage />} />
          <Route path="/active-bots" element={<ActiveBotsPage />} />
          <Route path="/trades" element={<TradeHistoryPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/invite-codes" element={<AdminOrManagerRoute><InviteCodesPage /></AdminOrManagerRoute>} />
          <Route path="/admin/users" element={<AdminRoute><UsersPage /></AdminRoute>} />
          <Route path="/tester" element={<AdminRoute><TesterPage /></AdminRoute>} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </BrowserRouter>
  );
}
