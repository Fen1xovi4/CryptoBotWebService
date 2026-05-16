import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { useAuthStore } from './stores/authStore';
import MainLayout from './components/Layout/MainLayout';
import LandingPage from './pages/LandingPage';
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
import PaymentPage from './pages/PaymentPage';
import AdminWalletsPage from './pages/AdminWalletsPage';
import AdminPaymentsPage from './pages/AdminPaymentsPage';
import GuestPaymentPage from './pages/GuestPaymentPage';
import SupportPage from './pages/SupportPage';
import SupportChatPage from './pages/SupportChatPage';
import AdminSupportPage from './pages/AdminSupportPage';
import AdminSupportChatPage from './pages/AdminSupportChatPage';
import ErrorBoundary from './components/ErrorBoundary';

function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  if (!isAuthenticated) return <Navigate to="/login" replace />;
  return <>{children}</>;
}

function AdminRoute({ children }: { children: React.ReactNode }) {
  const role = useAuthStore((s) => s.role);
  if (role !== 'Admin') return <Navigate to="/dashboard" replace />;
  return <>{children}</>;
}

function AdminOrManagerRoute({ children }: { children: React.ReactNode }) {
  const role = useAuthStore((s) => s.role);
  if (role !== 'Admin' && role !== 'Manager') return <Navigate to="/dashboard" replace />;
  return <>{children}</>;
}

export default function App() {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);

  return (
    <BrowserRouter>
      <Routes>
        {/* Public: Landing page at root */}
        <Route
          path="/"
          element={<LandingPage />}
        />
        <Route
          path="/login"
          element={isAuthenticated ? <Navigate to="/dashboard" replace /> : <LoginPage />}
        />
        <Route
          path="/register"
          element={isAuthenticated ? <Navigate to="/dashboard" replace /> : <RegisterPage />}
        />
        <Route path="/buy" element={<GuestPaymentPage />} />

        {/* Protected: Dashboard and app pages */}
        <Route
          element={
            <ProtectedRoute>
              <MainLayout />
            </ProtectedRoute>
          }
        >
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/workspace/:id" element={<WorkspaceDetailPage />} />
          <Route path="/proxies" element={<ProxiesPage />} />
          <Route path="/accounts" element={<AccountsPage />} />
          <Route path="/accounts/:id" element={<AccountDetailPage />} />
          <Route path="/strategies" element={<StrategiesPage />} />
          <Route path="/active-bots" element={<ErrorBoundary label="ActiveBotsPage"><ActiveBotsPage /></ErrorBoundary>} />
          <Route path="/trades" element={<TradeHistoryPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/payment" element={<PaymentPage />} />
          <Route path="/support" element={<SupportPage />} />
          <Route path="/support/:id" element={<SupportChatPage />} />
          <Route path="/invite-codes" element={<AdminOrManagerRoute><InviteCodesPage /></AdminOrManagerRoute>} />
          <Route path="/admin/users" element={<AdminRoute><UsersPage /></AdminRoute>} />
          <Route path="/admin/wallets" element={<AdminRoute><AdminWalletsPage /></AdminRoute>} />
          <Route path="/admin/payments" element={<AdminRoute><AdminPaymentsPage /></AdminRoute>} />
          <Route path="/admin/support" element={<AdminRoute><AdminSupportPage /></AdminRoute>} />
          <Route path="/admin/support/:id" element={<AdminRoute><AdminSupportChatPage /></AdminRoute>} />
          <Route path="/tester" element={<AdminRoute><TesterPage /></AdminRoute>} />
        </Route>

        {/* Catch-all */}
        <Route path="*" element={<Navigate to={isAuthenticated ? "/dashboard" : "/"} replace />} />
      </Routes>
    </BrowserRouter>
  );
}
